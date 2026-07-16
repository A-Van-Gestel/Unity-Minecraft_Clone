using System;
using Data;
using Editor.Validation.Framework;
using Jobs.BurstData;
using Placement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Editor.Validation.Placement.Framework
{
    /// <summary>
    /// The resolved result of a single player placement attempt, capturing the three tag-driven decisions the
    /// <c>PlayerInteraction.PlaceCursorBlocks</c> path composes (so a scenario can assert each independently).
    /// </summary>
    public readonly struct PlacementOutcome
    {
        /// <summary>
        /// True when the top-down ray <b>stopped</b> on a cell (<c>World.CheckForVoxel</c> reported a hit under the
        /// held block's skip mask). False means every cell in the column was air or skipped — the held block's
        /// <see cref="BlockType.placementCanReplaceTags"/> caused the ray to <i>tunnel through</i> every surface, so the
        /// player cannot aim at this column at all.
        /// </summary>
        public readonly bool DidHit;

        /// <summary>The cell the ray stopped on (the highest non-air, non-skipped cell from the top). Undefined when <see cref="DidHit"/> is false.</summary>
        public readonly Vector3Int HitCell;

        /// <summary>
        /// True when the held block <b>replaces</b> the hit cell (places into it) instead of landing in the cell
        /// above it — i.e. <see cref="PlacementResolver.ResolvesToReplace"/> returned true for the hit block.
        /// </summary>
        public readonly bool Replaces;

        /// <summary>The cell the block would actually occupy (the hit cell when <see cref="Replaces"/>, else the cell above it).</summary>
        public readonly Vector3Int PlaceCell;

        /// <summary>True when the resolved <see cref="PlaceCell"/> is in-world and not already occupied by a solid block.</summary>
        public readonly bool Placeable;

        /// <summary>Initializes a placement outcome.</summary>
        public PlacementOutcome(bool didHit, Vector3Int hitCell, bool replaces, Vector3Int placeCell, bool placeable)
        {
            DidHit = didHit;
            HitCell = hitCell;
            Replaces = replaces;
            PlaceCell = placeCell;
            Placeable = placeable;
        }

        /// <summary>
        /// The headline "place a block on top of the targeted surface succeeds" outcome: the ray stopped on a cell,
        /// the block did not replace it, and the destination cell above is free.
        /// </summary>
        public bool LandsOnTop => DidHit && !Replaces && Placeable;
    }

    /// <summary>
    /// Single-chunk, edit-mode harness that drives the <b>real</b> production placement seams
    /// (<c>World.CheckForVoxel</c>, <see cref="PlacementController"/>, <c>World.IsCellOccupiedForPlacement</c>) over a
    /// synthetic <see cref="ChunkData"/>, mirroring the tag-driven half of <c>PlayerInteraction.PlaceCursorBlocks</c>
    /// without a camera, toolbar, or geometric ray march. The march geometry is not where placement bugs live —
    /// the held block's <see cref="BlockType.placementCanReplaceTags"/> driving the skip mask and the replace decision is.
    /// <para>
    /// <b>World seam</b> reuses the <see cref="ValidationReflection"/> recipe established by
    /// <c>BehaviorTestWorld</c>: <c>AddComponent</c> a plain <see cref="World"/> (no <c>Awake</c> in edit mode),
    /// inject a stub <see cref="BlockDatabase"/> whose <c>blockTypes</c> is the supplied palette, assign a quiet
    /// <see cref="Settings"/> and a <see cref="WorldData"/>, inject a real <see cref="ChunkPoolManager"/> (section
    /// allocation needs it once <c>World.Instance</c> is set), and register a single all-air center chunk at the
    /// world origin so <c>worldData.GetVoxelState</c> resolves. <see cref="Dispose"/> restores the prior instance.
    /// </para>
    /// </summary>
    public sealed class PlacementTestWorld : IDisposable
    {
        /// <summary>The center chunk the placement queries read and the scenario seeds.</summary>
        public readonly ChunkData ChunkData;

        private readonly BlockType[] _palette;
        private readonly GameObject _worldGo;
        private readonly BlockDatabase _stubDatabase;
        private readonly World _world;
        private readonly World _previousInstance;
        private readonly PlacementController _controller;
        private bool _disposed;

        /// <summary>Reach for the harness probe — generous enough to march the full 0-127 column from any start Y.</summary>
        private const float PROBE_REACH = 256f;

        /// <summary>Ray-march step; matches the production <c>checkIncrement</c> default.</summary>
        private const float PROBE_INCREMENT = 0.05f;

        /// <summary>The palette backing <c>World.Instance.BlockTypes</c> — exposed so scenarios can read tag data by id.</summary>
        public BlockType[] Palette => _palette;

        /// <summary>
        /// Stands up the stub world + an all-air center chunk at the world origin, backed by the supplied palette.
        /// </summary>
        /// <param name="palette">The block palette assigned to the stub <see cref="BlockDatabase.blockTypes"/>; indices
        /// are the block ids the scenario places and queries (the real <see cref="BlockDatabase"/> for the data-audit
        /// and known-bug repros, or a controlled synthetic palette for the baselines).</param>
        /// <param name="originChunk">The WS-4 floating-origin anchor to drive the controller at. Defaults to the
        /// identity (0, 0), where Unity space and voxel space coincide — every pre-WS-4 scenario keeps its meaning.
        /// A non-zero value moves the world's voxel coordinates far out while the harness keeps addressing the same
        /// small Unity-space cells, which is what proves the controller actually converts.</param>
        public PlacementTestWorld(BlockType[] palette, ChunkCoord originChunk = default)
        {
            _previousInstance = World.Instance;
            try
            {
                _palette = palette;

                _stubDatabase = ScriptableObject.CreateInstance<BlockDatabase>();
                _stubDatabase.blockTypes = _palette;

                _worldGo = new GameObject("Placement_StubWorld");
                _world = _worldGo.AddComponent<World>();
                ValidationReflection.SetInstanceField(_world, "_blockDatabase", _stubDatabase);
                _world.settings = new Settings { enableLighting = false, enableWaterDiagnosticLogs = false };
                _world.worldData = new WorldData("PlacementTestWorld", 0);

                ValidationReflection.SetStaticProperty(typeof(World), nameof(World.Instance), _world);
                ValidationReflection.SetInstanceProperty(_world, nameof(World.ChunkPool),
                    new ChunkPoolManager(_worldGo.transform));

                // Single center chunk, seeded at the floating origin's chunk. Scenarios always address small
                // Unity-space cells (0-15); the controller offsets them onto this chunk's voxel coordinates, so the
                // whole model shifts with the origin and every existing scenario keeps its meaning at the identity.
                Vector2Int chunkVoxelPos = originChunk.ToVoxelOrigin();
                ChunkData = new ChunkData(chunkVoxelPos);
                _world.worldData.Chunks[chunkVoxelPos] = ChunkData;

                // The REAL production decision object the scenarios drive (no reimplementation in the harness).
                // The origin is injected, so the suite never touches the WorldOrigin global — no leak to restore.
                _controller = new PlacementController(_world, new Vector3Int(
                    chunkVoxelPos.x, 0, chunkVoxelPos.y));
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        /// <summary>Writes a block at a chunk-local / world position (origin chunk, so local == world for 0-15).</summary>
        /// <param name="x">Cell X (0-15).</param>
        /// <param name="y">Cell Y (0-127).</param>
        /// <param name="z">Cell Z (0-15).</param>
        /// <param name="id">Block id present in the palette.</param>
        /// <param name="meta">Raw metadata byte; defaults to 0.</param>
        public void SetBlock(int x, int y, int z, ushort id, byte meta = 0)
        {
            ChunkData.SetVoxel(x, y, z, BurstVoxelDataBitMapping.PackVoxelData(id, meta));
        }

        /// <summary>The Y the top-down probe ray starts from — above any block a scenario seeds in a 0-127 column.</summary>
        public const int DefaultStartY = 40;

        /// <summary>
        /// Resolves a placement attempt by a <b>top-down</b> probe ray down the (<paramref name="x"/>,
        /// <paramref name="z"/>) column — the player looking straight down at the column — by running the
        /// <b>real</b> <see cref="PlacementController.Probe"/> with a synthesized downward ray. The held block's skip
        /// mask, the replace-vs-land-adjacent decision, and the world placeability (bounds + occupancy + support) are
        /// all the production decision, not a harness reimplementation.
        /// <para>
        /// This exercises both production mechanisms in one model: a structural block the held item can replace gets
        /// skipped and the ray tunnels past it (the §03 bug when it should have been a target), while a soft block
        /// gets skipped and the item lands in its vacated cell (the intended "replace the plant" behavior).
        /// </para>
        /// </summary>
        /// <param name="heldId">The held block id, or <c>null</c> for an empty hand.</param>
        /// <param name="x">Column X (0-15).</param>
        /// <param name="z">Column Z (0-15).</param>
        /// <param name="startY">The Y to start the downward probe from; defaults to <see cref="DefaultStartY"/>.</param>
        /// <returns>The resolved <see cref="PlacementOutcome"/>.</returns>
        public PlacementOutcome ResolveTopDownPlacement(ushort? heldId, int x, int z, int startY = DefaultStartY)
        {
            BlockType held = heldId.HasValue ? _palette[heldId.Value] : null;

            // "Player looking straight down the column" — feed the real production probe a downward ray.
            Vector3 origin = new Vector3(x + 0.5f, startY + 0.5f, z + 0.5f);
            PlacementProbe probe = _controller.Probe(origin, Vector3.down, held, includeFluids: false, PROBE_REACH, PROBE_INCREMENT);

            return new PlacementOutcome(probe.DidHit, probe.HitCell, probe.Replaces, probe.PlaceCell, probe.WorldPlaceable);
        }

        /// <summary>
        /// Directly evaluates the production placement gate <see cref="PlacementController.CanPlaceAt"/> for an
        /// explicit place cell, bypassing the probe geometry. Used by scenarios that must control the block
        /// <i>directly beneath</i> the place cell — e.g. the <see cref="BlockTags.REQUIRES_SUPPORT"/>-over-water repro,
        /// which the held block's skip mask would otherwise tunnel the probe through. Still the REAL production
        /// function, fed synthetic inputs.
        /// </summary>
        /// <param name="heldId">The held block id, or <c>null</c> for an empty hand.</param>
        /// <param name="placeCell">The world voxel cell the block would occupy.</param>
        /// <returns>True if the production decision permits placement into the cell.</returns>
        public bool EvaluatePlacementAt(ushort? heldId, Vector3Int placeCell)
        {
            BlockType held = heldId.HasValue ? _palette[heldId.Value] : null;
            return _controller.CanPlaceAt(placeCell, held);
        }

        /// <summary>Restores the previous <c>World.Instance</c> and destroys every object the harness created.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            ValidationReflection.SetStaticProperty(typeof(World), nameof(World.Instance), _previousInstance);

            ChunkData?.Dispose();
            if (_worldGo != null) Object.DestroyImmediate(_worldGo);
            if (_stubDatabase != null) Object.DestroyImmediate(_stubDatabase);
        }
    }
}
