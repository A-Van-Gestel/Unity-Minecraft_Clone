using Data;
using Jobs.BurstData;
using MyBox;
using Physics;
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

    private VoxelRaycastResult _lastRaycastResult;

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
    }

    private void Update()
    {
        if (_world.InUI) return;

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

                byte meta = ComputePlacementMeta(placedBlockType, _lastRaycastResult);

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
    private byte ComputePlacementMeta(BlockType placedBlockType, VoxelRaycastResult raycastResult)
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
                // TODO: Defaulting roll to 0. Future improvement: derive roll from secondary player look axis or wrench tool.
                BurstVoxelMetadataUtility.EncodeFacing6Roll2(BurstVoxelMetadataUtility.Facing6FromLookVector(_playerCamera.forward), roll: 0),
            PlacementMetadataMode.PlayerLookAxis when placedBlockType.metadataSchema == MetadataSchema.HorizontalOnly =>
                BurstVoxelMetadataUtility.HorizontalOnlyFromLookVector(_playerCamera.forward),
            PlacementMetadataMode.SurfaceFacing when placedBlockType.metadataSchema == MetadataSchema.Facing6 =>
                BurstVoxelMetadataUtility.Facing6FromHitNormal(raycastResult.HitNormal),
            PlacementMetadataMode.SurfaceFacing when placedBlockType.metadataSchema == MetadataSchema.Facing6Roll2 =>
                BurstVoxelMetadataUtility.EncodeFacing6Roll2(BurstVoxelMetadataUtility.Facing6FromHitNormal(raycastResult.HitNormal), roll: 0),
            PlacementMetadataMode.SurfaceFacing when placedBlockType.metadataSchema == MetadataSchema.HorizontalOnly =>
                BurstVoxelMetadataUtility.HorizontalOnlyFromHitNormal(raycastResult.HitNormal),
            _ => placedBlockType.defaultMetadata,
        };
    }

    /// <summary>
    /// Centralized method to cast a ray from the player's camera to find a voxel.
    /// Uses mathematical fractional offsets to accurately determine the placed block face.
    /// </summary>
    /// <param name="overrideInteractWithFluids">If set, overrides the component's interactWithFluids toggle.</param>
    /// <returns>A VoxelRaycastResult struct containing information about the hit.</returns>
    public VoxelRaycastResult RaycastForVoxel(bool? overrideInteractWithFluids = null)
    {
        float step = checkIncrement;

        // Use the override if provided, otherwise fall back to the player's current setting
        bool checkFluids = overrideInteractWithFluids ?? interactWithFluids;

        while (step < reach)
        {
            Vector3 pos = _playerCamera.position + _playerCamera.forward * step;

            if (_world.CheckForVoxel(pos, checkFluids, includeNonSolid: true))
            {
                VoxelRaycastResult result = new VoxelRaycastResult { DidHit = true };
                result.HitPosition = new Vector3Int(Mathf.FloorToInt(pos.x), Mathf.FloorToInt(pos.y), Mathf.FloorToInt(pos.z));

                // Find the fractional position within the voxel to determine which face was intersected
                float xCheck = GetCoordinateOffset(pos.x);
                float yCheck = GetCoordinateOffset(pos.y);
                float zCheck = GetCoordinateOffset(pos.z);

                if (Mathf.Abs(xCheck) < Mathf.Abs(yCheck) && Mathf.Abs(xCheck) < Mathf.Abs(zCheck))
                    result.HitNormal = xCheck < 0 ? Vector3Int.right : Vector3Int.left;
                else if (Mathf.Abs(zCheck) < Mathf.Abs(yCheck) && Mathf.Abs(zCheck) < Mathf.Abs(xCheck))
                    result.HitNormal = zCheck < 0 ? Vector3Int.forward : Vector3Int.back;
                else
                    result.HitNormal = yCheck < 0 ? Vector3Int.up : Vector3Int.down;

                result.PlacePosition = result.HitPosition + result.HitNormal;

                return result;
            }

            step += checkIncrement;
        }

        // If we get here, we didn't hit anything.
        return new VoxelRaycastResult { DidHit = false };
    }

    /// <summary>
    /// Calculates the signed fractional distance from the nearest voxel boundary.
    /// </summary>
    private static float GetCoordinateOffset(float coordinate)
    {
        float frac = coordinate - Mathf.Floor(coordinate);
        if (frac > 0.5f) frac -= 1f;
        return frac;
    }

    private void PlaceCursorBlocks()
    {
        VoxelRaycastResult result = RaycastForVoxel();
        _lastRaycastResult = result;

        if (result.DidHit)
        {
            // If the targeted block is replaceable (e.g. grass), the place position
            // should be the block itself (replace it) rather than adjacent to it.
            VoxelState? hitState = _world.GetVoxelState(result.HitPosition);
            if (hitState.HasValue &&
                (hitState.Value.Properties.tags & BlockTags.REPLACEABLE) != 0)
            {
                result.PlacePosition = result.HitPosition;
            }

            highlightBlock.position = result.HitPosition;
            placeBlock.position = result.PlacePosition;

            // Check if the placement position is valid.
            // Using an AABB intersection to ensure the placed block does not overlap the player entity dynamically.
            Vector3 playerPosition = transform.position;
            VoxelRigidbody rb = _player.VoxelRigidbody;
            float extX = rb.CollisionHalfWidthX;
            float extZ = rb.CollisionHalfDepthZ;
            Vector3 pMin = new Vector3(playerPosition.x - extX, playerPosition.y, playerPosition.z - extZ);
            Vector3 pMax = new Vector3(playerPosition.x + extX, playerPosition.y + rb.collisionHeight, playerPosition.z + extZ);

            // The block AABB is exactly 1x1x1 at integer coordinates
            Vector3 bMin = result.PlacePosition;
            Vector3 bMax = result.PlacePosition + Vector3.one;

            bool isInsidePlayer =
                pMin.x < bMax.x && pMax.x > bMin.x &&
                pMin.y < bMax.y && pMax.y > bMin.y &&
                pMin.z < bMax.z && pMax.z > bMin.z;

            _blockPlaceable =
                !isInsidePlayer &&
                _world.worldData.IsVoxelInWorld(result.PlacePosition) &&
                !_world.CheckForCollision(result.PlacePosition) &&
                toolbar.slots[toolbar.slotIndex].ItemSlot.HasItem;

            // Set highlight objects active state
            _highlightBlocksParent.gameObject.SetActive(showHighlightBlocks);
            highlightBlock.gameObject.SetActive(true);
            placeBlock.gameObject.SetActive(_blockPlaceable);
        }
        else
        {
            // If we didn't hit a block, hide the highlights.
            highlightBlock.gameObject.SetActive(false);
            placeBlock.gameObject.SetActive(false);
        }
    }
}

// A struct to hold the results of our voxel raycast.
public struct VoxelRaycastResult
{
    public bool DidHit;
    public Vector3Int HitPosition;
    public Vector3Int HitNormal;
    public Vector3Int PlacePosition;
}
