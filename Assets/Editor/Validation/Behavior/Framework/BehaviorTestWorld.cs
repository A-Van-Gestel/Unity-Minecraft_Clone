using System;
using System.Collections.Generic;
using Data;
using Editor.Validation.Framework;
using Jobs.BurstData;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Editor.Validation.Behavior.Framework
{
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
    /// Step-4 six-neighbour re-activation. It
    /// deliberately omits the lighting/meshing/notify side effects (irrelevant to behavior parity) via
    /// <c>SetVoxel</c> instead of <c>ModifyVoxel</c>. Interior-only (Tier-1): with the empty <c>worldData</c>,
    /// a border-reaching neighbor query reads as "void" rather than crashing.
    /// </para>
    /// </summary>
    public sealed class BehaviorTestWorld : IDisposable
    {
        /// <summary>The synthetic chunk the behavior code reads and the harness mutates.</summary>
        public readonly ChunkData ChunkData;

        private readonly BlockType[] _palette;
        private readonly BlockType _inert;
        private readonly HashSet<Vector3Int> _activeVoxels = new HashSet<Vector3Int>();
        private readonly GameObject _worldGo;
        private readonly BlockDatabase _stubDatabase;
        private readonly World _world;
        private readonly World _previousInstance;
        private int _tick;
        private bool _disposed;

        /// <summary>Stands up the stub world + palette and an all-air chunk at the origin.</summary>
        public BehaviorTestWorld()
        {
            _previousInstance = World.Instance;
            try
            {
                _palette = TestBehaviorBlockPalette.Create();
                _inert = _palette[BlockIDs.Air]; // inert default for out-of-palette ids (see PaletteOf)

                _stubDatabase = ScriptableObject.CreateInstance<BlockDatabase>();
                _stubDatabase.blockTypes = _palette;

                _worldGo = new GameObject("Behavior_StubWorld");
                // AddComponent on a plain MonoBehaviour runs no Awake/OnEnable/OnValidate in edit mode, so no
                // world initialization fires; we only need the component as the typed Instance target.
                _world = _worldGo.AddComponent<World>();
                _world.blockDatabase = _stubDatabase;
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

                ChunkData = new ChunkData(Vector2Int.zero);

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
            // — see the BH-3 probe), exactly as Chunk.TickUpdate snapshots before iterating.
            List<Vector3Int> ordered = new List<Vector3Int>(_activeVoxels);
            List<VoxelEval> evals = new List<VoxelEval>(ordered.Count);
            Queue<VoxelMod> pending = new Queue<VoxelMod>();
            List<Vector3Int> toRemove = new List<Vector3Int>();

            foreach (Vector3Int pos in ordered)
            {
                // Behave returns a reused ThreadStatic list — deep-copy immediately before the next call clears it.
                List<VoxelMod> raw = BlockBehavior.Behave(ChunkData, pos);
                List<VoxelMod> copy = raw == null ? null : new List<VoxelMod>(raw);

                bool active = BlockBehavior.Active(ChunkData, pos);
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

        /// <summary>
        /// Applies a single mod, mirroring the state-affecting logic of <c>World.ApplyModifications</c> +
        /// <c>ChunkData.ModifyVoxel</c>: no-op early-out, placement-rule gate, active-set maintenance, the
        /// support-break cascade, and the Step-4 six-neighbour re-activation. Cross-chunk targets (and
        /// out-of-chunk neighbours) are skipped (Tier-1). Lighting/meshing/notify side effects
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

            // Six-neighbour re-activation — matches World.ApplyModifications Step 4: after any applied mod, the
            // World re-wakes every isActive neighbour of the modified cell. This is the parity-critical half — a
            // cell that quiesced and dropped from the active set is re-evaluated once an adjacent cell changes
            // (e.g. a fluid source re-woken by a freshly-placed flow neighbour). Without it the harness would
            // freeze behaviour the live engine never produces (a false-confidence golden). Interior-only (Tier-1):
            // a neighbour outside the chunk degrades to "void" here exactly as a cross-chunk read returns null in
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
                    return BlockTagUtility.CanReplace(newProps, oldProps);
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

            if (_worldGo != null) Object.DestroyImmediate(_worldGo);
            if (_stubDatabase != null) Object.DestroyImmediate(_stubDatabase);
        }

        /// <summary>Writes the private <c>World._tickCounter</c> backing field (TickCounter is read-only).</summary>
        private void SetTickCounter(int value) =>
            ValidationReflection.SetInstanceField(_world, "_tickCounter", value);
    }
}
