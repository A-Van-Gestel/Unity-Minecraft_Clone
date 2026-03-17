using Data;
using MyBox;
using Physics;
using UnityEngine;

public class PlayerInteraction : MonoBehaviour
{
    private Player _player;
    private World _world;
    private Transform _playerCamera;

    [Header("Block Interaction")]
    public bool showHighlightBlocks = true;

    public Transform highlightBlock;
    public Transform placeBlock;
    private Transform _highlightBlocksParent;

    /// <summary>
    /// Is current placeable block not inside the player, other solid block, outside the world and current itemSlot is not empty.
    /// </summary>
    private bool _blockPlaceable;

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
        _highlightBlocksParent = GameObject.Find("HighlightBlocks").GetComponent<Transform>();
    }

    private void Update()
    {
        if (_world.inUI) return;

        PlaceCursorBlocks();
        HandleBlockModificationInput();
    }

    private void HandleBlockModificationInput()
    {
        // PLACING & DESTROYING BLOCKS
        if (Input.GetKeyDown(_player.toggleBlockHighlightKey))
            showHighlightBlocks = !showHighlightBlocks;

        if (highlightBlock.gameObject.activeSelf)
        {
            // Destroy block.
            if (Input.GetMouseButtonDown(0))
            {
                _world.AddModification(new VoxelMod(highlightBlock.position.ToVector3Int(), blockId: BlockIDs.Air)
                {
                    ImmediateUpdate = true,
                });
            }

            // Place block.
            if (Input.GetMouseButtonDown(1))
            {
                // Don't place blocks inside the player or other voxels or when current itemSlot is empty by returning early.
                if (!_blockPlaceable) return;

                UIItemSlot itemSlot = toolbar.slots[toolbar.slotIndex];
                _world.AddModification(new VoxelMod(placeBlock.position.ToVector3Int(), blockId: itemSlot.ItemSlot.Stack.ID)
                {
                    Orientation = _player.orientation,
                    ImmediateUpdate = true,
                });
                itemSlot.ItemSlot.Take(1);
            }
        }
    }


    /// <summary>
    /// Centralized method to cast a ray from the player's camera to find a voxel.
    /// Uses mathematical fractional offsets to accurately determine the placed block face.
    /// </summary>
    /// <returns>A VoxelRaycastResult struct containing information about the hit.</returns>
    public VoxelRaycastResult RaycastForVoxel()
    {
        float step = checkIncrement;

        while (step < reach)
        {
            Vector3 pos = _playerCamera.position + _playerCamera.forward * step;

            if (_world.CheckForVoxel(pos))
            {
                VoxelRaycastResult result = new VoxelRaycastResult { DidHit = true };
                result.HitPosition = new Vector3Int(Mathf.FloorToInt(pos.x), Mathf.FloorToInt(pos.y), Mathf.FloorToInt(pos.z));

                // Find the fractional position within the voxel to determine which face was intersected
                float xCheck = GetCoordinateOffset(pos.x);
                float yCheck = GetCoordinateOffset(pos.y);
                float zCheck = GetCoordinateOffset(pos.z);

                if (Mathf.Abs(xCheck) < Mathf.Abs(yCheck) && Mathf.Abs(xCheck) < Mathf.Abs(zCheck))
                    result.PlacePosition = result.HitPosition + (xCheck < 0 ? Vector3Int.right : Vector3Int.left);
                else if (Mathf.Abs(zCheck) < Mathf.Abs(yCheck) && Mathf.Abs(zCheck) < Mathf.Abs(xCheck))
                    result.PlacePosition = result.HitPosition + (zCheck < 0 ? Vector3Int.forward : Vector3Int.back);
                else
                    result.PlacePosition = result.HitPosition + (yCheck < 0 ? Vector3Int.up : Vector3Int.down);

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

        if (result.DidHit)
        {
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
                !_world.CheckForVoxel(result.PlacePosition) &&
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
    public Vector3Int PlacePosition;
}
