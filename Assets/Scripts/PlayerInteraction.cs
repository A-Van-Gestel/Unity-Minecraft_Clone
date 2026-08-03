using Data;
using Helpers;
using Jobs.BurstData;
using Physics;
using Placement;
using Unity.Mathematics;
using UnityEngine;

public class PlayerInteraction : MonoBehaviour
{
    private Player _player;
    private World _world;
    private Transform _playerCamera;
    private InputManager _input;

    [Header("Block Interaction")]
    public bool showHighlightBlocks = true;

    public bool interactWithFluids = false;

    public Transform highlightBlock;
    public Transform placeBlock;
    private Transform _highlightBlocksParent;

    /// <summary>Mesh child of <see cref="highlightBlock"/>, shaped to the targeted block's collision volume.</summary>
    private Transform _highlightCube;

    /// <summary>Mesh child of <see cref="placeBlock"/>, shaped to the held block's collision volume.</summary>
    private Transform _placeCube;

    /// <summary>Authored scale of <see cref="_highlightCube"/>, preserved as a multiplier (see <c>CacheHighlightCube</c>).</summary>
    private Vector3 _highlightCubeBias;

    /// <summary>Authored scale of <see cref="_placeCube"/>, preserved as a multiplier (see <c>CacheHighlightCube</c>).</summary>
    private Vector3 _placeCubeBias;

    /// <summary>A whole cell, in cell-local space — the outline's shape when the targeted block cannot be resolved.</summary>
    private static readonly Bounds s_fullCellBounds = new Bounds(new Vector3(0.5f, 0.5f, 0.5f), Vector3.one);

    /// <summary>
    /// Is current placeable block not inside the player, other solid block, outside the world and current itemSlot is not empty.
    /// </summary>
    private bool _blockPlaceable;

    private PlacementController _placement;
    private PlacementProbe _lastProbe;

    /// <summary>
    /// The floating origin <see cref="_lastProbe"/> was resolved under. Kept beside the probe so a block
    /// modification is addressed in the same coordinate frame its cells were found in, rather than re-reading a
    /// global that may have re-anchored since.
    /// </summary>
    private Vector3Int _lastProbeOrigin;

    [Tooltip("Maximum distance the player can interact with blocks.")]
    public float reach = 8f;

    [Header("UI Interaction")]
    public Toolbar toolbar;

    private void Awake()
    {
        _player = GetComponent<Player>();
        _playerCamera = Camera.main.transform;
    }

    private void Start()
    {
        _world = World.Instance;
        _input = InputManager.Instance;
        _highlightBlocksParent = GameObject.Find("HighlightBlocks").GetComponent<Transform>();
        _placement = new PlacementController(_world);

        CacheHighlightCube(highlightBlock, out _highlightCube, out _highlightCubeBias);
        CacheHighlightCube(placeBlock, out _placeCube, out _placeCubeBias);
    }

    /// <summary>
    /// Caches a highlight box's mesh child and its authored scale. The parent sits on the targeted cell's minimum
    /// corner and the child carries the centring offset, so shaping a highlight to a sub-voxel block drives the
    /// child. Its authored scale is kept as a per-box multiplier rather than assumed: the two boxes ship with
    /// different values (the block outline is inflated slightly to beat z-fighting against the surface it hugs, the
    /// placement preview is not, since it is drawn in open air).
    /// </summary>
    /// <param name="box">The highlight box root, positioned on the cell corner.</param>
    /// <param name="cube">The mesh child to shape, or null when the box has none.</param>
    /// <param name="bias">The child's authored scale, applied on top of the block's size.</param>
    private static void CacheHighlightCube(Transform box, out Transform cube, out Vector3 bias)
    {
        cube = box != null && box.childCount > 0 ? box.GetChild(0) : null;
        bias = cube != null ? cube.localScale : Vector3.one;
    }

    private void Update()
    {
        if (World.InUI || WorldLaunchState.IsAutomatedMode) return;

        PlaceCursorBlocks();
        HandleBlockModificationInput();
    }

    private void HandleBlockModificationInput()
    {
        // PLACING & DESTROYING BLOCKS
        if (_input.ToggleBlockHighlightPressed)
            showHighlightBlocks = !showHighlightBlocks;

        if (highlightBlock.gameObject.activeSelf)
        {
            // Destroy block.
            if (_input.AttackPressed)
            {
                // VoxelMod.GlobalPosition is voxel space (it is persisted), so the probe's Unity-space cell converts.
                // Read from the probe rather than the highlight transform: the cell is already exact there, with no
                // float round-trip to re-derive it from.
                Vector3Int breakVoxel = ToVoxelMod(_lastProbe.HitCell);

                // TF-14: the fence gates edits, not aiming — a block reached through the wall highlights but
                // cannot be broken. (Placement is gated inside the probe via PlacementController.CanPlaceAt.)
                if (_world.IsVoxelInsideBorder(breakVoxel))
                {
                    _world.AddModification(new VoxelMod(breakVoxel, blockId: BlockIDs.Air)
                    {
                        ImmediateUpdate = true,
                    });
                }
            }

            // Place block.
            if (_input.UsePressed)
            {
                // Don't place blocks inside the player or other voxels or when current itemSlot is empty by returning early.
                if (!_blockPlaceable) return;

                UIItemSlot itemSlot = toolbar.slots[toolbar.slotIndex];
                ushort placedBlockId = itemSlot.ItemSlot.Stack.ID;
                BlockType placedBlockType = _world.BlockTypes[placedBlockId];

                byte meta = ComputePlacementMeta(placedBlockType, _lastProbe.HitNormal);

                _world.AddModification(new VoxelMod(ToVoxelMod(_lastProbe.PlaceCell), placedBlockId)
                {
                    Meta = meta,
                    ImmediateUpdate = true,
                });
                itemSlot.ItemSlot.Take(1);
            }
        }
    }


    /// <summary>
    /// Converts a Unity-space cell from the placement probe into the absolute voxel cell a
    /// <see cref="VoxelMod"/> is addressed in — <c>VoxelMod.GlobalPosition</c> is persisted, so it must never
    /// carry a Unity-space value.
    /// </summary>
    /// <param name="unityCell">The Unity-space cell resolved by the probe.</param>
    /// <returns>The absolute voxel cell to modify.</returns>
    // Uses the probe's own origin, not a fresh global read: the cell and the offset must come from the same frame,
    // or a re-anchor between the probe and the click would address the edit to the wrong voxel.
    private Vector3Int ToVoxelMod(Vector3Int unityCell) => unityCell + _lastProbeOrigin;

    /// <summary>
    /// Computes the metadata byte for a freshly-placed block based on its
    /// configured <see cref="PlacementMetadataMode"/>. Fluids always start at
    /// meta=0 so <c>BlockBehavior.Fluids</c> can fill them from a source on
    /// the first simulation tick.
    /// </summary>
    private byte ComputePlacementMeta(BlockType placedBlockType, int3 hitNormal)
    {
        if (placedBlockType.fluidType != FluidType.None)
        {
            return 0;
        }

        return placedBlockType.placementMetadataMode switch
        {
            PlacementMetadataMode.PlayerYawCardinal when placedBlockType.metadataSchema == MetadataSchema.Axis3 =>
                BurstVoxelMetadataUtility.Axis3FromLegacyWorldOrientation(_player.orientation),
            PlacementMetadataMode.PlayerYawCardinal =>
                BurstVoxelDataBitMapping.BuildMetaLegacy(
                    _player.orientation, fluidLevel: 0, isFluid: false),
            PlacementMetadataMode.PlayerLookAxis when placedBlockType.metadataSchema == MetadataSchema.Axis3 =>
                BurstVoxelMetadataUtility.DominantAxisFromLookVector(_playerCamera.forward),
            PlacementMetadataMode.PlayerLookAxis when placedBlockType.metadataSchema == MetadataSchema.Facing6 =>
                BurstVoxelMetadataUtility.Facing6FromLookVector(_playerCamera.forward),
            PlacementMetadataMode.PlayerLookAxis when placedBlockType.metadataSchema == MetadataSchema.Facing6Roll2 =>
                ComputeFacing6Roll2Meta(_playerCamera.forward),
            PlacementMetadataMode.PlayerLookAxis when placedBlockType.metadataSchema == MetadataSchema.HorizontalOnly =>
                BurstVoxelMetadataUtility.HorizontalOnlyFromLookVector(_playerCamera.forward),
            PlacementMetadataMode.SurfaceFacing when placedBlockType.metadataSchema == MetadataSchema.Facing6 =>
                BurstVoxelMetadataUtility.Facing6FromHitNormal(hitNormal),
            PlacementMetadataMode.SurfaceFacing when placedBlockType.metadataSchema == MetadataSchema.Facing6Roll2 =>
                ComputeFacing6Roll2Meta(_playerCamera.forward, hitNormal),
            PlacementMetadataMode.SurfaceFacing when placedBlockType.metadataSchema == MetadataSchema.HorizontalOnly =>
                BurstVoxelMetadataUtility.HorizontalOnlyFromHitNormal(hitNormal),
            _ => placedBlockType.defaultMetadata,
        };
    }

    /// <summary>
    /// Computes Facing6Roll2 metadata from the player's look direction (for
    /// <see cref="PlacementMetadataMode.PlayerLookAxis"/>). Facing is derived from
    /// the dominant look axis; roll aligns the block's +Y toward the player when
    /// placed on a floor/ceiling.
    /// </summary>
    private static byte ComputeFacing6Roll2Meta(Vector3 lookForward)
    {
        byte facing = BurstVoxelMetadataUtility.Facing6FromLookVector(lookForward);
        byte roll = BurstVoxelMetadataUtility.RollFromLookVector(facing, lookForward);
        return BurstVoxelMetadataUtility.EncodeFacing6Roll2(facing, roll);
    }

    /// <summary>
    /// Computes Facing6Roll2 metadata from the hit surface normal (for
    /// <see cref="PlacementMetadataMode.SurfaceFacing"/>). Facing is derived from
    /// the surface normal; roll aligns the block's +Y toward the player when
    /// placed on a floor/ceiling.
    /// </summary>
    private static byte ComputeFacing6Roll2Meta(Vector3 lookForward, int3 hitNormal)
    {
        byte facing = BurstVoxelMetadataUtility.Facing6FromHitNormal(hitNormal);
        byte roll = BurstVoxelMetadataUtility.RollFromLookVector(facing, lookForward);
        return BurstVoxelMetadataUtility.EncodeFacing6Roll2(facing, roll);
    }

    /// <summary>
    /// Centralized method to cast a ray from the player's camera to find a voxel.
    /// The reported face is the one the traversal crossed to enter the hit cell, not a derivation from the hit
    /// point — so it stays exact on corner hits, where the placed block's orientation metadata depends on it.
    /// </summary>
    /// <param name="overrideInteractWithFluids">If set, overrides the component's interactWithFluids toggle.</param>
    /// <param name="skipTags">Block tags the ray should pass through (derived from the held block's canReplaceTags).</param>
    /// <returns>A VoxelRaycastResult struct containing information about the hit.</returns>
    public VoxelRaycastResult RaycastForVoxel(bool? overrideInteractWithFluids = null,
        BlockTags skipTags = BlockTags.NONE)
    {
        // Use the override if provided, otherwise fall back to the player's current setting.
        bool checkFluids = overrideInteractWithFluids ?? interactWithFluids;

        // Read the origin fresh — never cached — so a re-anchor takes effect on the very next raycast.
        if (_placement.MarchRay(_playerCamera.position, _playerCamera.forward, checkFluids, skipTags, reach,
                WorldOrigin.OriginVoxel,
                out Vector3Int hitCell, out int3 hitNormal, out Vector3Int adjacentCell))
        {
            return new VoxelRaycastResult
            {
                DidHit = true,
                HitPosition = hitCell,
                HitNormal = hitNormal,
                PlacePosition = adjacentCell,
            };
        }

        return new VoxelRaycastResult { DidHit = false };
    }

    private void PlaceCursorBlocks()
    {
        // When holding a block, the placement ray passes through blocks it can replace (e.g. ocean floor through
        // water); when holding nothing, heldBlock stays null so all blocks are targetable for punching. The whole
        // tag-driven decision (skip mask, replace-vs-adjacent, world placeability incl. support) lives in the
        // PlacementController — only the camera ray, the player-overlap veto, and the highlight visuals stay here.
        UIItemSlot heldSlot = toolbar.slots[toolbar.slotIndex];
        BlockType heldBlock = heldSlot.ItemSlot.HasItem
            ? _world.BlockTypes[heldSlot.ItemSlot.Stack.ID]
            : null;

        // Read the origin fresh each frame — never cached at construction — so a re-anchor takes effect immediately.
        _lastProbeOrigin = WorldOrigin.OriginVoxel;
        PlacementProbe probe = _placement.Probe(_playerCamera.position, _playerCamera.forward, heldBlock,
            interactWithFluids, reach, _lastProbeOrigin);
        _lastProbe = probe;

        if (!probe.DidHit)
        {
            // If we didn't hit a block, hide the highlights.
            highlightBlock.gameObject.SetActive(false);
            placeBlock.gameObject.SetActive(false);
            return;
        }

        highlightBlock.position = probe.HitCell;
        placeBlock.position = probe.PlaceCell;

        // VQ-3: both boxes hug the block's real volume, not its cell — targeting a half-slab is exact, so a
        // full-cube outline around it would read as a bug. The place preview uses the metadata the block would
        // actually be placed with, so a slab previews as a slab on the correct half.
        // The outline is shaped on every path, including the one where the block cannot be resolved: it is shown
        // unconditionally below, so a skipped update would leave the previous block's silhouette on screen.
        Vector3Int hitVoxel = ToVoxelMod(probe.HitCell);
        Bounds hitBounds = _world.TryGetVoxel(hitVoxel.x, hitVoxel.y, hitVoxel.z, out VoxelState hitVoxelState)
            ? BlockCollisionBoundsUtility.GetBounds(_world.BlockTypes[hitVoxelState.ID], hitVoxelState.Meta,
                Vector3.zero)
            : s_fullCellBounds;
        ShapeHighlight(_highlightCube, _highlightCubeBias, hitBounds);

        // The preview needs no such fallback: an empty hand leaves heldBlock null, which also clears
        // _blockPlaceable below and hides the box entirely, so its shape is never seen while stale.
        if (heldBlock != null)
            ShapeHighlight(_placeCube, _placeCubeBias, BlockCollisionBoundsUtility.GetBounds(
                heldBlock, ComputePlacementMeta(heldBlock, probe.HitNormal), Vector3.zero));

        // The controller already decided world placeability (bounds + occupancy + support). The player-AABB overlap
        // is player-entity state, so it stays here as a final veto: the placed block must not intersect the player.
        _blockPlaceable =
            probe.WorldPlaceable &&
            !PlaceCellOverlapsPlayer(probe.PlaceCell) &&
            heldSlot.ItemSlot.HasItem;

        // Set highlight objects active state
        _highlightBlocksParent.gameObject.SetActive(showHighlightBlocks);
        highlightBlock.gameObject.SetActive(true);
        placeBlock.gameObject.SetActive(_blockPlaceable);
    }

    /// <summary>
    /// Shapes a highlight box's mesh child to a block volume expressed in cell-local space — the parent already
    /// sits on the cell corner, so a full-cube block reproduces the authored center and scale exactly and only
    /// sub-voxel blocks move.
    /// </summary>
    /// <param name="cube">The mesh child to shape; ignored when null.</param>
    /// <param name="bias">The child's authored scale, applied on top of the block's size.</param>
    /// <param name="localBounds">The volume to hug, in cell-local space.</param>
    private static void ShapeHighlight(Transform cube, Vector3 bias, Bounds localBounds)
    {
        if (cube == null) return;

        cube.localPosition = localBounds.center;
        cube.localScale = Vector3.Scale(localBounds.size, bias);
    }

    /// <summary>
    /// True when a 1×1×1 block occupying <paramref name="placeCell"/> would intersect the player's collision AABB —
    /// the player-entity veto layered on top of the controller's world placeability.
    /// </summary>
    /// <param name="placeCell">The cell a block would be placed in.</param>
    private bool PlaceCellOverlapsPlayer(Vector3Int placeCell)
    {
        Vector3 playerPosition = transform.position;
        VoxelRigidbody rb = _player.VoxelRigidbody;
        float extX = rb.CollisionHalfWidthX;
        float extZ = rb.CollisionHalfDepthZ;
        Vector3 pMin = new Vector3(playerPosition.x - extX, playerPosition.y, playerPosition.z - extZ);
        Vector3 pMax = new Vector3(playerPosition.x + extX, playerPosition.y + rb.collisionHeight, playerPosition.z + extZ);

        // The block AABB is exactly 1x1x1 at integer coordinates.
        Vector3 bMin = placeCell;
        Vector3 bMax = placeCell + Vector3.one;

        return pMin.x < bMax.x && pMax.x > bMin.x &&
               pMin.y < bMax.y && pMax.y > bMin.y &&
               pMin.z < bMax.z && pMax.z > bMin.z;
    }
}

// A struct to hold the results of our voxel raycast.
public struct VoxelRaycastResult
{
    public bool DidHit;
    public Vector3Int HitPosition;
    public int3 HitNormal;
    public Vector3Int PlacePosition;
}
