using Data;
using Jobs.BurstData;
using MyBox;
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

    /// <summary>
    /// Is current placeable block not inside the player, other solid block, outside the world and current itemSlot is not empty.
    /// </summary>
    private bool _blockPlaceable;

    private PlacementController _placement;
    private PlacementProbe _lastProbe;

    [Tooltip("Distance between each ray-cast check, lower value means better accuracy")]
    public float checkIncrement = 0.05f;

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
        _placement = new PlacementController(_world, reach, checkIncrement);
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
                _world.AddModification(new VoxelMod(highlightBlock.position.ToVector3Int(), blockId: BlockIDs.Air)
                {
                    ImmediateUpdate = true,
                });
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

                _world.AddModification(new VoxelMod(placeBlock.position.ToVector3Int(), placedBlockId)
                {
                    Meta = meta,
                    ImmediateUpdate = true,
                });
                itemSlot.ItemSlot.Take(1);
            }
        }
    }


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
    /// Uses mathematical fractional offsets to accurately determine the placed block face.
    /// </summary>
    /// <param name="overrideInteractWithFluids">If set, overrides the component's interactWithFluids toggle.</param>
    /// <param name="skipTags">Block tags the ray should pass through (derived from the held block's canReplaceTags).</param>
    /// <returns>A VoxelRaycastResult struct containing information about the hit.</returns>
    public VoxelRaycastResult RaycastForVoxel(bool? overrideInteractWithFluids = null,
        BlockTags skipTags = BlockTags.NONE)
    {
        // Use the override if provided, otherwise fall back to the player's current setting.
        bool checkFluids = overrideInteractWithFluids ?? interactWithFluids;

        if (_placement.MarchRay(_playerCamera.position, _playerCamera.forward, checkFluids, skipTags,
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

        PlacementProbe probe = _placement.Probe(_playerCamera.position, _playerCamera.forward, heldBlock, interactWithFluids);
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
