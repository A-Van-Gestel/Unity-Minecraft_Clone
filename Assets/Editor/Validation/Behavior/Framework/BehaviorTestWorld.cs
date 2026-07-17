using System;
using System.Collections.Generic;
using System.Text;
using Data;
using Editor.Validation.Framework;
using Helpers;
using Jobs;
using Jobs.BurstData;
using Unity.Collections;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Editor.Validation.Behavior.Framework
{
    /// <summary>
    /// Selects how <see cref="BehaviorTestWorld"/> orders active-voxel evaluation within a tick — the modeled
    /// production tick driver. <see cref="Legacy"/> reproduces today's single-set traversal; <see cref="SplitFamily"/>
    /// reproduces the TG-4 Phase 1 per-behavior-family split (evaluate all grass, then all fluids). The
    /// <b>BH-D1</b> differential pits the two against each other under the §4.3 canonicalization.
    /// </summary>
    public enum TickDriver
    {
        /// <summary>One monolithic active set, iterated in <c>HashSet</c> enumeration order (today's path).</summary>
        Legacy,

        /// <summary>Per-behavior-family buckets, iterated grass-then-fluid (the TG-4 Phase 1 path).</summary>
        SplitFamily,

        /// <summary>
        /// TG-4 Phase 3 hybrid: grass-then-fluid order (as <see cref="SplitFamily"/>), but Tier-1 <b>interior</b> fluid
        /// voxels are evaluated by the real Burst <see cref="FluidTickJob"/> instead of the managed
        /// <see cref="BlockBehavior"/>; border fluids + grass stay managed. BH-D1[L|F] pits this against
        /// <see cref="Legacy"/> to prove the Burst port emits a byte-identical stream.
        /// </summary>
        FluidBurstHybrid,

        /// <summary>
        /// TG-4 Phase 4b full halo: grass-then-fluid order, but <b>every</b> fluid voxel (interior AND border) is
        /// evaluated by the Burst <see cref="FluidTickJob"/>, border voxels reading the per-tick neighbor halo
        /// gathered from the seeded neighbor chunks. Grass stays managed. BH-D1[L|H] pits this against
        /// <see cref="Legacy"/> over the BH-4 cross-chunk fixtures to prove the halo border port is byte-identical.
        /// </summary>
        FluidBurstHalo,

        /// <summary>
        /// TG-4 Phase 4b Y-band: identical to <see cref="FluidBurstHalo"/> but the per-tick gather + reads are
        /// restricted to the tight active-fluid Y-band (<c>FluidBurstTicker.RunFluids(useBand: true)</c>) instead of
        /// full chunk height. <b>BH-D1[H|HB]</b> pits this directly against <see cref="FluidBurstHalo"/> to isolate
        /// band-edge correctness from halo correctness — the two must be byte-identical (the band drops no read the
        /// full-height gather served); <b>BH-D1[L|HB]</b> pits it against <see cref="Legacy"/> as the end-to-end gate.
        /// </summary>
        FluidBurstHaloBand,
    }

    /// <summary>
    /// Single-chunk, edit-mode harness that drives the <b>real</b> <see cref="BlockBehavior"/> tick path
    /// (<see cref="BlockBehavior.Behave"/> + <see cref="BlockBehavior.Active"/>) over a synthetic
    /// <see cref="ChunkData"/>, mirroring <c>Chunk.TickUpdate</c> + <c>World.ProcessTickUpdates</c> +
    /// <c>World.ApplyModifications</c> without a live world.
    /// <para>
    /// <b>World seam (reuses the MH-6 reflection-stub recipe via <see cref="ValidationReflection"/>):</b>
    /// <c>VoxelState.Properties</c> reads <c>World.Instance.BlockTypes[id]</c>, so the harness
    /// <c>AddComponent</c>s a plain <see cref="World"/> (no <c>Awake</c> runs in edit mode), points its public
    /// <c>blockDatabase</c> at a stub <see cref="BlockDatabase"/> whose <c>blockTypes</c> is
    /// <see cref="TestBehaviorBlockPalette"/>, assigns a quiet <see cref="Settings"/> and an empty
    /// <see cref="WorldData"/> (so out-of-chunk neighbor queries degrade to null instead of NRE), injects a real
    /// <see cref="ChunkPoolManager"/> into the private <c>ChunkPool</c> (section allocation needs it once
    /// <c>World.Instance</c> is set), and drives the private static <c>World.Instance</c> setter + the private
    /// <c>_tickCounter</c> field by reflection. <see cref="Dispose"/> restores the previous instance.
    /// </para>
    /// <para>
    /// <b>Apply path:</b> mods emitted by <see cref="BlockBehavior.Behave"/> are drained FIFO and applied
    /// through <see cref="ApplyMod"/>, which faithfully mirrors the state-affecting half of
    /// <c>World.ApplyModifications</c> + <c>ChunkData.ModifyVoxel</c>: the <c>oldPacked == newPacked</c> no-op
    /// early-out, the <see cref="BlockTagUtility.CanReplace"/> / <see cref="ReplacementRule"/> placement gate,
    /// the active-set add/remove contract, the <see cref="BlockTags.REQUIRES_SUPPORT"/> break cascade, and the
    /// Step-4 six-neighbor re-activation. It
    /// deliberately omits the lighting/meshing/notify side effects (irrelevant to behavior parity) via
    /// <c>SetVoxel</c> instead of <c>ModifyVoxel</c>. Interior-only (Tier-1): with the empty <c>worldData</c>,
    /// a border-reaching neighbor query reads as "void" rather than crashing.
    /// </para>
    /// </summary>
    public sealed class BehaviorTestWorld : IDisposable
    {
        /// <summary>The synthetic chunk the behavior code reads and the harness mutates.</summary>
        public readonly ChunkData ChunkData;

        /// <summary>The blittable palette blob (mirrors <c>World.JobDataManager.BlockTypesJobData</c>) for driving the real fluid jobs in edit-mode validation.</summary>
        internal NativeArray<BlockTypeJobData> BlockTypesJob => _blockTypesJob;

        /// <summary>
        /// The stub chunk store backing cross-chunk neighbor reads (holds the <see cref="SetNeighborBlock"/>-seeded
        /// neighbor chunks). Needed to drive the Phase-4b halo runner (<see cref="FluidBurstTicker.RunFluids"/> /
        /// <see cref="FluidBurstTicker.ScheduleFluids"/>) directly from the parallel-determinism gate.
        /// </summary>
        internal WorldData WorldData => _world.worldData;

        private readonly BlockType[] _palette;
        private readonly BlockType _inert;

        private NativeArray<BlockTypeJobData> _blockTypesJob; // built from the palette for the FluidBurstHybrid driver

        // The REAL production runner — the FluidBurstHybrid driver drives this (not a hand-copy) so BH-D1[L|F]
        // exercises the shipped partition/snapshot/job/ModsPerSource path, not a twin that could drift from it.
        private readonly FluidBurstTicker _fluidTicker = new FluidBurstTicker();

        // The ticker reads ChunkData.ActiveFluidsBucket; the harness's own active model (_activeVoxels) is mirrored
        // into it each tick by SyncFluidBucketToActives. _bucketedFluids tracks what we put there so stale fluids
        // can be evicted.
        private readonly HashSet<Vector3Int> _bucketedFluids = new HashSet<Vector3Int>();
        private readonly List<Vector3Int> _bucketSyncScratch = new List<Vector3Int>();

        private readonly HashSet<Vector3Int> _activeVoxels = new HashSet<Vector3Int>();

        // BH-4 (TG-4 Phase 4b): static cross-chunk READ context. Neighbor chunks seeded here are registered in
        // _world.worldData.Chunks so the center chunk's border voxels resolve real neighbor data through the
        // production GetState → worldData.GetVoxelState path (no shim). Neighbors are read-only context — they are
        // NOT ticked and their voxels are NOT registered active; an UNSEEDED neighbor coord resolves to null (void),
        // which IS the missing/ungenerated-neighbor case. Keyed by voxel origin; disposed in Dispose.
        private readonly Dictionary<Vector2Int, ChunkData> _neighbors = new Dictionary<Vector2Int, ChunkData>();
        private readonly GameObject _worldGo;
        private readonly BlockDatabase _stubDatabase;
        private readonly World _world;
        private readonly World _previousInstance;
        private int _tick;
        private bool _disposed;

        /// <summary>
        /// Which tick driver this world models. Defaults to <see cref="TickDriver.Legacy"/> so existing goldens run
        /// the original traversal unchanged; set before <see cref="RunTicks"/> to model the TG-4 split path for BH-D1.
        /// </summary>
        public TickDriver Driver = TickDriver.Legacy;

        /// <summary>
        /// Stands up the stub world + palette and an all-air center chunk at <paramref name="centerChunkVoxelOrigin"/>.
        /// </summary>
        /// <param name="centerChunkVoxelOrigin">
        /// The center chunk's voxel origin (its <see cref="ChunkData.Position"/>). Defaults to the world origin
        /// <c>(0,0)</c> — what every single-chunk (Tier-1) fixture uses, where −X/−Z reads fall outside the world
        /// (void) exactly as before. BH-4 cross-chunk fixtures pass an <b>interior</b> origin so all 8 neighbor coords
        /// satisfy <see cref="WorldData.IsVoxelInWorld"/> and can be seeded via <see cref="SetNeighborBlock"/>.
        /// </param>
        public BehaviorTestWorld(Vector2Int centerChunkVoxelOrigin = default)
        {
            _previousInstance = World.Instance;
            try
            {
                _palette = TestBehaviorBlockPalette.Create();
                _inert = _palette[BlockIDs.Air]; // inert default for out-of-palette ids (see PaletteOf)

                // Blittable copy of the palette for the FluidBurstHybrid driver's FluidTickJob (mirrors
                // World.JobDataManager.BlockTypesJobData, built from the same BlockType source).
                _blockTypesJob = new NativeArray<BlockTypeJobData>(_palette.Length, Allocator.Persistent);
                for (int i = 0; i < _palette.Length; i++)
                    _blockTypesJob[i] = new BlockTypeJobData(_palette[i]);

                _stubDatabase = ScriptableObject.CreateInstance<BlockDatabase>();
                _stubDatabase.blockTypes = _palette;

                _worldGo = new GameObject("Behavior_StubWorld");
                // AddComponent on a plain MonoBehaviour runs no Awake/OnEnable/OnValidate in edit mode, so no
                // world initialization fires; we only need the component as the typed Instance target.
                _world = _worldGo.AddComponent<World>();
                // World no longer exposes a public block-database field; inject the stub into the private
                // backing field directly (Awake is bypassed in edit mode, so the loader never overwrites it).
                ValidationReflection.SetInstanceField(_world, "_blockDatabase", _stubDatabase);
                _world.settings = new Settings { enableLighting = false, enableWaterDiagnosticLogs = false };
                // Empty WorldData: ChunkData.GetState routes out-of-chunk reads to worldData.GetVoxelState,
                // which returns null for absent chunks — so a border-reaching neighbor query degrades to "void"
                // instead of dereferencing a null worldData (the prior NRE footgun).
                _world.worldData = new WorldData("BehaviorTestWorld", 0);

                // GetNewSection() allocates sections via World.Instance.ChunkPool whenever World.Instance is
                // non-null (which it must be here). Awake normally builds the pool but is bypassed, so inject a
                // real ChunkPoolManager (ctor only wires lazy pools — edit-mode safe).
                ValidationReflection.SetInstanceProperty(_world, nameof(World.ChunkPool),
                    new ChunkPoolManager(_worldGo.transform));

                ChunkData = new ChunkData(centerChunkVoxelOrigin);

                ValidationReflection.SetStaticProperty(typeof(World), nameof(World.Instance), _world);
                SetTickCounter(0);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        /// <summary>Number of voxels currently registered as active (ticked each pass).</summary>
        public int ActiveVoxelCount => _activeVoxels.Count;

        /// <summary>
        /// Writes a block at a chunk-local position and, mirroring initial active-voxel registration, marks it
        /// active if its block type's <c>isActive</c> flag is set. Use during scenario setup.
        /// </summary>
        /// <param name="x">Chunk-local X (0-15).</param>
        /// <param name="y">Chunk-local Y (0-127).</param>
        /// <param name="z">Chunk-local Z (0-15).</param>
        /// <param name="id">Block ID (a real <see cref="BlockIDs"/> value present in the palette).</param>
        /// <param name="meta">Raw metadata byte (e.g. a fluid level); defaults to 0.</param>
        public void SetBlock(int x, int y, int z, ushort id, byte meta = 0)
        {
            ChunkData.SetVoxel(x, y, z, BurstVoxelDataBitMapping.PackVoxelData(id, meta));
            if (PaletteOf(id).isActive)
                _activeVoxels.Add(new Vector3Int(x, y, z));
        }

        /// <summary>
        /// BH-4 (TG-4 Phase 4b): writes a block into a <b>neighbor</b> chunk as static cross-chunk READ context, so
        /// the center chunk's border voxels read real neighbor data through the production
        /// <c>ChunkData.GetState → worldData.GetVoxelState</c> path. The neighbor chunk is lazily created at the
        /// center's chunk coord offset by <paramref name="dChunkX"/>/<paramref name="dChunkZ"/> and registered in the
        /// stub <see cref="WorldData.Chunks"/>. Neighbor voxels are <b>not</b> ticked and <b>not</b> registered active
        /// (read-only context); to model a missing/ungenerated neighbor, simply do not seed it (its coord resolves to
        /// null = void, matching <c>GetVoxelState</c>). Requires an interior center origin (see the ctor) so the
        /// neighbor coord is in-world.
        /// </summary>
        /// <param name="dChunkX">Neighbor chunk offset on X (−1, 0, or +1).</param>
        /// <param name="dChunkZ">Neighbor chunk offset on Z (−1, 0, or +1).</param>
        /// <param name="lx">Neighbor-local X (0-15).</param>
        /// <param name="ly">Neighbor-local Y (0-127).</param>
        /// <param name="lz">Neighbor-local Z (0-15).</param>
        /// <param name="id">Block ID (a real <see cref="BlockIDs"/> value present in the palette).</param>
        /// <param name="meta">Raw metadata byte (e.g. a fluid level); defaults to 0.</param>
        public void SetNeighborBlock(int dChunkX, int dChunkZ, int lx, int ly, int lz, ushort id, byte meta = 0)
        {
            Vector2Int origin = new Vector2Int(
                ChunkData.Position.x + dChunkX * VoxelData.ChunkWidth,
                ChunkData.Position.y + dChunkZ * VoxelData.ChunkWidth);

            if (!_neighbors.TryGetValue(origin, out ChunkData neighbor))
            {
                neighbor = new ChunkData(origin);
                _neighbors[origin] = neighbor;
                _world.worldData.SetChunk(origin, neighbor); // the seam GetVoxelState resolves
            }

            neighbor.SetVoxel(lx, ly, lz, BurstVoxelDataBitMapping.PackVoxelData(id, meta));
        }

        /// <summary>
        /// Runs <paramref name="tickCount"/> behavior ticks and returns the captured <see cref="BehaviorSnapshot"/>.
        /// </summary>
        /// <param name="tickCount">Number of ticks to run.</param>
        public BehaviorSnapshot RunTicks(int tickCount)
        {
            List<TickRecord> ticks = new List<TickRecord>(tickCount);
            for (int i = 0; i < tickCount; i++)
                ticks.Add(RunOneTick());
            return new BehaviorSnapshot(ticks);
        }

        /// <summary>
        /// Executes one tick: advance the tick salt, evaluate every active voxel (<see cref="BlockBehavior.Behave"/>
        /// then <see cref="BlockBehavior.Active"/>) reading start-of-tick state, drop the now-inactive voxels, then
        /// drain the emitted mods FIFO — matching production's "Behave reads, mods applied afterward" ordering and
        /// <c>World.ApplyModifications</c>'s queue drain (the support cascade re-enqueues into the same queue).
        /// </summary>
        private TickRecord RunOneTick()
        {
            // Mirror ProcessTickUpdates: the tick counter is bumped once per pass, BEFORE any Behave runs.
            _tick++;
            SetTickCounter(_tick);

            // Snapshot the active set so the apply pass can mutate it safely (HashSet enum order is deterministic
            // — see the BH-3 probe), exactly as Chunk.TickUpdate snapshots before iterating. The traversal ORDER is
            // chosen by the modeled driver (Legacy single-set vs SplitFamily per-family) — the variable BH-D1 tests.
            List<Vector3Int> ordered = OrderActives();

            // FluidBurstHybrid/FluidBurstHalo: precompute the job-ticked fluids' (mods, active) via the real
            // FluidTickJob over a pre-tick snapshot, keyed by position — mirroring production's Chunk.TickFluidsHybrid.
            // Hybrid: only Tier-1 interior (border + grass = managed). Halo: ALL fluids (border via the neighbor halo;
            // grass = managed). Null for the managed-only drivers.
            Dictionary<Vector3Int, FluidJobResult> jobResults =
                Driver == TickDriver.FluidBurstHybrid ? RunFluidJob(halo: false) :
                Driver == TickDriver.FluidBurstHalo ? RunFluidJob(halo: true) :
                Driver == TickDriver.FluidBurstHaloBand ? RunFluidJob(halo: true, band: true) : null;

            List<VoxelEval> evals = new List<VoxelEval>(ordered.Count);
            Queue<VoxelMod> pending = new Queue<VoxelMod>();
            List<Vector3Int> toRemove = new List<Vector3Int>();

            foreach (Vector3Int pos in ordered)
            {
                List<VoxelMod> copy;
                bool active;

                if (jobResults != null && jobResults.TryGetValue(pos, out FluidJobResult jr))
                {
                    // Interior fluid — its Behave/Active were computed by the Burst job (over the same pre-tick state).
                    copy = jr.Mods;
                    active = jr.Active;
                }
                else
                {
                    // Behave returns a reused ThreadStatic list — deep-copy immediately before the next call clears it.
                    List<VoxelMod> raw = BlockBehavior.Behave(ChunkData, pos);
                    copy = raw == null ? null : new List<VoxelMod>(raw);
                    active = BlockBehavior.Active(ChunkData, pos);
                }

                evals.Add(new VoxelEval(pos, active, copy));

                if (copy != null)
                    foreach (VoxelMod mod in copy)
                        pending.Enqueue(mod);
                if (!active) toRemove.Add(pos);
            }

            foreach (Vector3Int pos in toRemove)
                _activeVoxels.Remove(pos);

            // Drain FIFO like World.ApplyModifications; ApplyMod may enqueue cascade mods into the same queue.
            while (pending.Count > 0)
                ApplyMod(pending.Dequeue(), pending);

            return new TickRecord(_tick, evals);
        }

        /// <summary>The Burst job's per-voxel result for one interior fluid: its emitted mods (null if none) and active flag.</summary>
        private readonly struct FluidJobResult
        {
            public readonly List<VoxelMod> Mods;
            public readonly bool Active;

            public FluidJobResult(List<VoxelMod> mods, bool active)
            {
                Mods = mods;
                Active = active;
            }
        }

        /// <summary>
        /// Drives the <b>real</b> production runner <see cref="FluidBurstTicker.RunInteriorFluids"/> over this
        /// chunk's Tier-1 interior fluid voxels and returns each interior voxel's (mods, active) keyed by position.
        /// Unlike a hand-rolled copy, this exercises the shipped partition + snapshot + <see cref="FluidTickJob"/>
        /// + <c>ModsPerSource</c> split, so BH-D1[L|F] guards the actual orchestration (not a twin that could
        /// drift). The harness's own active model is first mirrored into <see cref="ChunkData.ActiveFluidsBucket"/>
        /// (the set the runner reads) by <see cref="SyncFluidBucketToActives"/>; results are mapped back to
        /// positions via the runner's <see cref="FluidBurstTicker.InteriorIndices"/>. Border fluids and grass stay
        /// on the managed path.
        /// </summary>
        /// <param name="halo">
        /// False = the Phase-3 interior-only hybrid (<see cref="FluidBurstTicker.RunInteriorFluids"/>, border managed);
        /// true = the Phase-4b full halo (<see cref="FluidBurstTicker.RunFluids"/>, every fluid job-ticked via the
        /// neighbor halo gathered from the harness's seeded neighbor chunks in <c>worldData</c>).
        /// </param>
        /// <param name="band">
        /// TG-4 Phase 4b Y-band: only meaningful when <paramref name="halo"/> is true. False = full-height gather;
        /// true = the tight active-fluid Y-band gather (<see cref="FluidBurstTicker.RunFluids"/> <c>useBand</c>). The
        /// <see cref="TickDriver.FluidBurstHaloBand"/> driver sets it so BH-D1[H|HB] diffs band vs full directly.
        /// </param>
        private Dictionary<Vector3Int, FluidJobResult> RunFluidJob(bool halo, bool band = false)
        {
            Dictionary<Vector3Int, FluidJobResult> results = new Dictionary<Vector3Int, FluidJobResult>();

            // Mirror the harness active set into ChunkData's fluid bucket (the runner's input), then run it.
            SyncFluidBucketToActives();
            if (halo)
                _fluidTicker.RunFluids(ChunkData, _tick, _blockTypesJob, _world.worldData, band);
            else
                _fluidTicker.RunInteriorFluids(ChunkData, _tick, _blockTypesJob);

            NativeList<int> interiorIndices = _fluidTicker.InteriorIndices;
            if (interiorIndices.Length == 0)
                return results;

            NativeList<VoxelMod> mods = _fluidTicker.Mods;
            NativeList<int> perSource = _fluidTicker.ModsPerSource;

            HashSet<int> inactiveSet = new HashSet<int>();
            foreach (int k in _fluidTicker.InactiveInterior)
                inactiveSet.Add(k);

            // Split the flat mod list back into per-source runs (ModsPerSource is parallel to InteriorIndices), keyed
            // by each source's position. Use null for a zero-mod source to match Behave's "return null when no mods".
            int modCursor = 0;
            for (int i = 0; i < interiorIndices.Length; i++)
            {
                int count = perSource[i];
                List<VoxelMod> srcMods = null;
                if (count > 0)
                {
                    srcMods = new List<VoxelMod>(count);
                    for (int k = 0; k < count; k++)
                        srcMods.Add(mods[modCursor + k]);
                }

                modCursor += count;
                int flat = interiorIndices[i];
                ChunkMath.GetLocalPositionFromFlattenedIndex(flat, out int x, out int y, out int z);
                results[new Vector3Int(x, y, z)] = new FluidJobResult(srcMods, !inactiveSet.Contains(flat));
            }

            return results;
        }

        /// <summary>
        /// Mirrors the harness's active-voxel model (<see cref="_activeVoxels"/>) into
        /// <see cref="ChunkData.ActiveFluidsBucket"/> — the set <see cref="FluidBurstTicker.RunInteriorFluids"/>
        /// partitions — so the real runner sees exactly the active fluids the harness is tracking. The harness uses
        /// <c>SetVoxel</c> (which bypasses the bucket maintenance <c>ModifyVoxel</c> does), so the bucket must be
        /// reconciled each tick: evict previously-bucketed voxels that are no longer active fluids, then register
        /// every active fluid (idempotent). Only fluids are mirrored; grass stays on the managed path.
        /// </summary>
        private void SyncFluidBucketToActives()
        {
            _bucketSyncScratch.Clear();
            foreach (Vector3Int pos in _bucketedFluids)
            {
                ushort id = BurstVoxelDataBitMapping.GetId(ChunkData.GetVoxel(pos.x, pos.y, pos.z));
                if (!_activeVoxels.Contains(pos) || ChunkData.ClassifyFamily(id) != ChunkData.BehaviorFamily.Fluid)
                {
                    ChunkData.RemoveActiveVoxel(pos);
                    _bucketSyncScratch.Add(pos);
                }
            }

            foreach (Vector3Int pos in _bucketSyncScratch)
                _bucketedFluids.Remove(pos);

            foreach (Vector3Int pos in _activeVoxels)
            {
                ushort id = BurstVoxelDataBitMapping.GetId(ChunkData.GetVoxel(pos.x, pos.y, pos.z));
                if (ChunkData.ClassifyFamily(id) == ChunkData.BehaviorFamily.Fluid && _bucketedFluids.Add(pos))
                    ChunkData.AddActiveVoxel(pos, id);
            }
        }

        /// <summary>
        /// Produces this tick's active-voxel evaluation order for the selected <see cref="Driver"/>:
        /// <list type="bullet">
        /// <item><see cref="TickDriver.Legacy"/> — the single active set in <c>HashSet</c> enumeration order
        /// (today's <c>Chunk.TickUpdate</c>).</item>
        /// <item><see cref="TickDriver.SplitFamily"/> — partitioned by behavior family and concatenated
        /// grass-then-fluid, modeling the TG-4 Phase 1 per-family buckets. Within a family the relative order is the
        /// same deterministic set-enumeration order, so the only change vs Legacy is the cross-family interleaving —
        /// exactly the benign reorder BH-D1 must prove §4.3-equivalent.</item>
        /// </list>
        /// Any active voxel that classifies to no known family is appended last, so none is ever dropped.
        /// </summary>
        private List<Vector3Int> OrderActives()
        {
            if (Driver == TickDriver.Legacy)
                return new List<Vector3Int>(_activeVoxels);

            List<Vector3Int> grass = new List<Vector3Int>();
            List<Vector3Int> fluids = new List<Vector3Int>();
            List<Vector3Int> other = null;

            // Classify via the PRODUCTION classifier (ChunkData.ClassifyFamily) so this driver models the real
            // per-family partition by construction — a third family added to production can't silently drift from
            // the harness's model. The stub World.Instance.BlockTypes is the test palette, so the lookup matches.
            foreach (Vector3Int pos in _activeVoxels)
            {
                ushort id = BurstVoxelDataBitMapping.GetId(ChunkData.GetVoxel(pos.x, pos.y, pos.z));
                switch (ChunkData.ClassifyFamily(id))
                {
                    case ChunkData.BehaviorFamily.Grass: grass.Add(pos); break;
                    case ChunkData.BehaviorFamily.Fluid: fluids.Add(pos); break;
                    default:
                        (other ??= new List<Vector3Int>()).Add(pos);
                        break;
                }
            }

            List<Vector3Int> ordered = new List<Vector3Int>(_activeVoxels.Count);
            ordered.AddRange(grass);
            ordered.AddRange(fluids);
            if (other != null) ordered.AddRange(other);
            return ordered;
        }

        /// <summary>
        /// Canonical dump of the chunk's non-air voxels (packed) in ascending (x, y, z) order — the BH-D1
        /// final-state byte-identity backstop. Two driver runs that emit §4.3-equivalent streams must also leave
        /// identical voxel state; this surfaces any divergence (e.g. a differing keep/drop) an equal mod set hides.
        /// </summary>
        public string DumpVoxels()
        {
            StringBuilder sb = new StringBuilder();
            for (int x = 0; x < VoxelData.ChunkWidth; x++)
            for (int y = 0; y < VoxelData.ChunkHeight; y++)
            for (int z = 0; z < VoxelData.ChunkWidth; z++)
            {
                uint packed = ChunkData.GetVoxel(x, y, z);
                if (BurstVoxelDataBitMapping.GetId(packed) == BlockIDs.Air) continue;
                sb.Append('(').Append(x).Append(',').Append(y).Append(',').Append(z).Append(")=")
                    .Append(packed).Append('\n');
            }

            return sb.ToString();
        }

        /// <summary>
        /// Applies a single mod, mirroring the state-affecting logic of <c>World.ApplyModifications</c> +
        /// <c>ChunkData.ModifyVoxel</c>: no-op early-out, placement-rule gate, active-set maintenance, the
        /// support-break cascade, and the Step-4 six-neighbor re-activation. Cross-chunk targets (and
        /// out-of-chunk neighbors) are skipped (Tier-1). Lighting/meshing/notify side effects
        /// are intentionally omitted (not part of the behavior surface under test).
        /// </summary>
        /// <param name="mod">The modification to apply.</param>
        /// <param name="pending">The active mod queue, so a support-break cascade can enqueue follow-up mods.</param>
        private void ApplyMod(VoxelMod mod, Queue<VoxelMod> pending)
        {
            int lx = mod.GlobalPosition.x - ChunkData.Position.x;
            int ly = mod.GlobalPosition.y;
            int lz = mod.GlobalPosition.z - ChunkData.Position.y;

            if (!ChunkData.IsVoxelInChunk(lx, ly, lz))
                return; // cross-chunk spread — Tier-1 scenarios never produce it

            uint oldPacked = ChunkData.GetVoxel(lx, ly, lz);
            uint newPacked = BurstVoxelDataBitMapping.PackVoxelData(mod.ID, mod.Meta);

            // No-op guard — matches ChunkData.ModifyVoxel: an unchanged write does NOT touch the active set.
            if (oldPacked == newPacked)
                return;

            ushort oldId = BurstVoxelDataBitMapping.GetId(oldPacked);
            BlockType oldProps = PaletteOf(oldId);
            BlockType newProps = PaletteOf(mod.ID);

            // Placement-rule gate — matches World.ApplyModifications. A rejected mod is dropped, exactly as in
            // production, so the harness never applies a placement the engine would refuse.
            // DEFENSIVE PARITY (BH-7, verified 2026-06-20): no current block behavior emits a CanReplace-rejected
            // mod (every emission targets Air / a non-solid-or-REPLACEABLE cell / the same fluid / convertible
            // dirt — all placements the live engine performs). This gate is kept faithful so the apply path stays
            // correct if TG-4/TG-5 or a new behavior changes emission; it is therefore unreachable through Behave
            // and intentionally unguarded by a scenario. Do not remove without re-checking reachability.
            if (!CanPlace(mod, oldId, oldProps, newProps))
                return;

            bool oldProvidedSupport = oldProps.ProvidesSupport;

            ChunkData.SetVoxel(lx, ly, lz, newPacked);

            // Active-set maintenance — matches ChunkData.ModifyVoxel.
            Vector3Int local = new Vector3Int(lx, ly, lz);
            if (newProps.isActive) _activeVoxels.Add(local);
            else if (oldProps.isActive) _activeVoxels.Remove(local);

            // Support-break cascade — matches World.ApplyModifications: if a support block became non-solid,
            // break the REQUIRES_SUPPORT block above it (enqueued into the same drain, ImmediateUpdate carried).
            // DEFENSIVE PARITY (BH-7, verified 2026-06-20): no current behavior emits a solid→non-solid mod
            // (grass↔dirt are solid→solid; all fluid mods replace non-solid cells), so this branch is unreachable
            // through Behave. Kept for faithfulness if TG-4/TG-5 or a new behavior changes emission.
            if (oldProvidedSupport && !newProps.ProvidesSupport)
            {
                VoxelState? above = ChunkData.GetState(new Vector3Int(lx, ly + 1, lz));
                if (above.HasValue && (PaletteOf(above.Value.ID).tags & BlockTags.REQUIRES_SUPPORT) != 0)
                {
                    Vector3Int aboveGlobal = new Vector3Int(mod.GlobalPosition.x, mod.GlobalPosition.y + 1, mod.GlobalPosition.z);
                    pending.Enqueue(new VoxelMod(aboveGlobal, BlockIDs.Air) { ImmediateUpdate = mod.ImmediateUpdate });
                }
            }

            // Six-neighbor re-activation — matches World.ApplyModifications Step 4: after any applied mod, the
            // World re-wakes every isActive neighbor of the modified cell. This is the parity-critical half — a
            // cell that quiesced and dropped from the active set is re-evaluated once an adjacent cell changes
            // (e.g. a fluid source re-woken by a freshly-placed flow neighbor). Without it the harness would
            // freeze behavior the live engine never produces (a false-confidence golden). Interior-only (Tier-1):
            // a neighbor outside the chunk degrades to "void" here exactly as a cross-chunk read returns null in
            // production, so it is skipped rather than woken.
            foreach (Vector3Int offset in VoxelData.FaceChecks)
            {
                int nx = lx + offset.x;
                int ny = ly + offset.y;
                int nz = lz + offset.z;

                if (!ChunkData.IsVoxelInChunk(nx, ny, nz))
                    continue;

                ushort neighborId = BurstVoxelDataBitMapping.GetId(ChunkData.GetVoxel(nx, ny, nz));
                if (PaletteOf(neighborId).isActive)
                    _activeVoxels.Add(new Vector3Int(nx, ny, nz));
            }
        }

        /// <summary>
        /// Mirrors the placement-rule decision in <c>World.ApplyModifications</c>: Air is a break (allowed unless
        /// the target is <see cref="BlockTags.UNBREAKABLE"/>); otherwise the mod's <see cref="ReplacementRule"/>
        /// decides, with <see cref="ReplacementRule.Default"/> deferring to <see cref="BlockTagUtility.CanReplace"/>.
        /// </summary>
        private static bool CanPlace(VoxelMod mod, ushort oldId, BlockType oldProps, BlockType newProps)
        {
            if (mod.ID == BlockIDs.Air)
                return (oldProps.tags & BlockTags.UNBREAKABLE) == 0;

            switch (mod.Rule)
            {
                case ReplacementRule.ForcePlace:
                    return (oldProps.tags & BlockTags.UNBREAKABLE) == 0;
                case ReplacementRule.OnlyReplaceAir:
                    return oldId == BlockIDs.Air;
                default:
                    // Behavior emissions are Live mods → resolve against the placement replacement mask
                    // (matches World.ApplyModifications for VoxelModSource.Live).
                    return BlockTagUtility.CanReplaceForPlacement(newProps, oldProps);
            }
        }

        /// <summary>
        /// Palette lookup that returns the inert default for any id outside the palette range, so a behavior that
        /// emits — or a scenario that places — a block not modeled by <see cref="TestBehaviorBlockPalette"/> is
        /// treated as inert rather than throwing <see cref="IndexOutOfRangeException"/> (which the suite would
        /// mis-score as a behavior regression). Extend the palette to model such a block deliberately.
        /// </summary>
        private BlockType PaletteOf(ushort id) => id < _palette.Length ? _palette[id] : _inert;

        /// <summary>Restores the previous <c>World.Instance</c> and destroys every object the harness created.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            ValidationReflection.SetStaticProperty(typeof(World), nameof(World.Instance), _previousInstance);

            _fluidTicker.Dispose();
            foreach (ChunkData neighbor in _neighbors.Values)
                neighbor.Dispose();
            if (_blockTypesJob.IsCreated) _blockTypesJob.Dispose();
            if (_worldGo != null) Object.DestroyImmediate(_worldGo);
            if (_stubDatabase != null) Object.DestroyImmediate(_stubDatabase);
        }

        /// <summary>Writes the private <c>World._tickCounter</c> backing field (TickCounter is read-only).</summary>
        private void SetTickCounter(int value) =>
            ValidationReflection.SetInstanceField(_world, "_tickCounter", value);
    }
}
