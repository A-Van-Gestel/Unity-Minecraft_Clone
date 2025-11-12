using Data;
using MyBox;
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

    void Awake()
    {
        _player = GetComponent<Player>();
        _playerCamera = Camera.main.transform;
    }

    void Start()
    {
        _world = World.Instance;
        _highlightBlocksParent = GameObject.Find("HighlightBlocks").GetComponent<Transform>();
    }

    void Update()
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
                _world.AddModification(new VoxelMod(highlightBlock.position.ToVector3Int(), blockId: 0)
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

                // DESTROY HIGHLIGHT BLOCK
                result.HitPosition = new Vector3Int(Mathf.FloorToInt(pos.x), Mathf.FloorToInt(pos.y), Mathf.FloorToInt(pos.z));

                // PLACE HIGHLIGHT BLOCK
                // Calculate place block position based on smallest x, y, z value, using HitPosition position as your origin.
                float xCheck = pos.x % 1;
                if (xCheck > 0.5f)
                    xCheck -= 1;
                float yCheck = pos.y % 1;
                if (yCheck > 0.5f)
                    yCheck -= 1;
                float zCheck = pos.z % 1;
                if (zCheck > 0.5f)
                    zCheck -= 1;

                if (Mathf.Abs(xCheck) < Mathf.Abs(yCheck) && Mathf.Abs(xCheck) < Mathf.Abs(zCheck))
                {
                    // place block on x-axis
                    if (xCheck < 0)
                        result.PlacePosition = result.HitPosition + Vector3Int.right;
                    else
                        result.PlacePosition = result.HitPosition + Vector3Int.left;
                }
                else if (Mathf.Abs(zCheck) < Mathf.Abs(yCheck) && Mathf.Abs(zCheck) < Mathf.Abs(xCheck))
                {
                    // place block on z axis
                    if (zCheck < 0)
                        result.PlacePosition = result.HitPosition + Vector3Int.forward;
                    else
                        result.PlacePosition = result.HitPosition + Vector3Int.back;
                }
                else
                {
                    // place block on y-axis by default
                    if (yCheck < 0)
                        result.PlacePosition = result.HitPosition + Vector3Int.up;
                    else
                        result.PlacePosition = result.HitPosition + Vector3Int.down;
                }

                return result;
            }

            step += checkIncrement;
        }

        // If we get here, we didn't hit anything.
        return new VoxelRaycastResult { DidHit = false };
    }

    private void PlaceCursorBlocks()
    {
        VoxelRaycastResult result = RaycastForVoxel();

        if (result.DidHit)
        {
            highlightBlock.position = result.HitPosition;
            placeBlock.position = result.PlacePosition;

            // Check if the placement position is valid.
            Vector3 playerPosition = transform.position;
            Vector3Int playerCoord = new Vector3Int(Mathf.FloorToInt(playerPosition.x), Mathf.FloorToInt(playerPosition.y), Mathf.FloorToInt(playerPosition.z));

            _blockPlaceable =
                result.PlacePosition != playerCoord && // Not inside player's feet
                result.PlacePosition != playerCoord + Vector3Int.up && // Not inside player's head
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