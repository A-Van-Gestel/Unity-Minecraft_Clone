using Helpers;
using UnityEngine;

public class ChunkPoolManager
{
    private readonly Transform _worldParent;

    // --- Pools ---
    private readonly DynamicPool<Chunk> _chunkPool;
    private readonly DynamicPool<GameObject> _borderPool;

    // --- Statistics ---
    public int ActiveChunks => _chunkPool.ActiveCount;
    public int PooledChunks => _chunkPool.PooledCount;
    public int PooledBorders => _borderPool.PooledCount;

    // --- Cleanup Settings ---
    private int _targetViewDistance;
    private const float POOL_BUFFER_PERCENTAGE = 1.25f; // Keep 25% extra as buffer

    public int targetViewDistance => _targetViewDistance;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChunkPoolManager"/> class.
    /// </summary>
    /// <param name="worldTransform">The main World transform component where the new chunks will be created under.</param>
    public ChunkPoolManager(Transform worldTransform)
    {
        _worldParent = worldTransform;

        // Initialize Chunk Pool
        _chunkPool = new DynamicPool<Chunk>(
            createFunc: () => new Chunk(new ChunkCoord(0, 0)), // Dummy coord, Reset() called later
            destroyAction: (chunk) => chunk.Destroy(),
            onReturnAction: (chunk) => chunk.Release()
        );

        // Initialize Border Pool
        // Note: createFunc returns null because Borders require context (prefab/pos) 
        // that isn't available here. Creation is handled in GetBorder().
        _borderPool = new DynamicPool<GameObject>(
            createFunc: () => null,
            destroyAction: (go) => Object.Destroy(go),
            onReturnAction: (go) => go.SetActive(false)
        );
    }

    /// <summary>
    /// Updates the target view distance used to calculate the ideal pool size.
    /// Call this when settings change or on startup.
    /// </summary>
    public void SetTargetViewDistance(int viewDistance)
    {
        _targetViewDistance = viewDistance;
    }

    /// <summary>
    /// Processes the "Drip Feed" cleanup. Call this from World.Update().
    /// </summary>
    public void Update()
    {
        // Calculate Target Pool Size
        // Area = (Dist * 2 + 1)^2. 
        // We multiply by Buffer to prevent thrashing (destroying then immediately creating).
        int chunksNeeded = _targetViewDistance * 2 + 1;
        int maxPoolSize = Mathf.CeilToInt(chunksNeeded * POOL_BUFFER_PERCENTAGE);

        // Delegate cleanup to the generic pools
        _chunkPool.UpdatePruning(maxPoolSize);
        _borderPool.UpdatePruning(maxPoolSize);
    }

    #region Chunk Logic

    /// <summary>
    /// Retrieves a chunk from the pool or creates a new one if the pool is empty.
    /// Resets the chunk state for the new coordinate.
    /// </summary>
    public Chunk Get(ChunkCoord coord)
    {
        // The Pool.Get() handles the Stack pop. 
        // We handle the Chunk-specific setup (Reset) here.
        Chunk chunk = _chunkPool.Get();
        chunk.Reset(coord);
        return chunk;
    }

    /// <summary>
    /// Returns a chunk to the pool for reuse.
    /// </summary>
    public void Return(Chunk chunk)
    {
        _chunkPool.Return(chunk);
    }

    #endregion

    #region Border Logic

    /// <summary>
    /// Retrieves a Border GameObject from the pool or instantiates a new one.
    /// </summary>
    public GameObject GetBorder(GameObject prefab, Vector3 position, Transform parent)
    {
        // 1. Try to get from pool.
        // If pool is empty, DynamicPool calls createFunc which returns null.
        GameObject border = _borderPool.Get();

        // 2. If null (empty pool), create new instance.
        if (border == null)
        {
            border = Object.Instantiate(prefab, position, Quaternion.identity, parent);
        }

        // 3. Apply state
        // We set parent/position again to ensure pooled objects are reset correctly.
        border.transform.SetParent(parent);
        border.transform.position = position;

        // Update name
        ChunkCoord coord = new ChunkCoord(position);
        border.name = $"Border {coord.X}, {coord.Z}";

        // Set active
        border.SetActive(true);

        return border;
    }

    public void ReturnBorder(GameObject border)
    {
        _borderPool.Return(border);
    }

    #endregion

    /// <summary>
    /// Destroys all pooled chunks and cleans up resources.
    /// Call this on World destroy.
    /// </summary>
    public void Clear()
    {
        _chunkPool.Clear();
        _borderPool.Clear();

        Debug.Log("[ChunkPoolManager] All Chunks in the ChunkPool have been disposed of..");
    }
}
