using System.Collections.Generic;
using UnityEngine;

public class ChunkPoolManager
{
    private readonly Transform _worldParent;

    // --- Pools ---
    // Pool for the main Chunk logic/visuals
    private readonly Stack<Chunk> _chunkPool = new Stack<Chunk>();

    // Pool for the Debug Border GameObjects
    private readonly Stack<GameObject> _borderPool = new Stack<GameObject>();

    // --- Statistics ---
    public int ActiveChunks => _activeChunkCount;
    public int PooledChunks => _chunkPool.Count;
    public int PooledBorders => _borderPool.Count;

    private int _activeChunkCount = 0;

    // --- Cleanup Settings ---
    private int _targetViewDistance;
    private float _cleanupTimer = 0f;
    private const float CLEANUP_INTERVAL = 0.05f; // Check 20 times a second
    private const float POOL_BUFFER_PERCENTAGE = 1.25f; // Keep 25% extra as buffer

    public int targetViewDistance => _targetViewDistance;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChunkPoolManager"/> class.
    /// </summary>
    /// <param name="worldTransform">The main World transform component where the new chunks will be created under.</param>
    public ChunkPoolManager(Transform worldTransform)
    {
        _worldParent = worldTransform;
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
        // Don't run every single frame to save micro-cycles, unless pool is MASSIVE
        _cleanupTimer += Time.deltaTime;
        if (_cleanupTimer < CLEANUP_INTERVAL) return;
        _cleanupTimer = 0;

        // Calculate Target
        // Area = (Dist * 2 + 1)^2. 
        // We multiply by Buffer to prevent thrashing (destroying then immediately creating).
        int chunksNeeded = _targetViewDistance * 2 + 1;
        int maxPoolSize = Mathf.CeilToInt(chunksNeeded * POOL_BUFFER_PERCENTAGE);

        // --- Cleanup Chunks ---
        if (_chunkPool.Count > maxPoolSize)
        {
            // Destroy ONE item per tick to spread the GC cost
            Chunk c = _chunkPool.Pop();
            c.Destroy();
        }

        // --- Cleanup Borders ---
        // Borders are cheap, but we apply the same logic
        if (_borderPool.Count > maxPoolSize)
        {
            GameObject b = _borderPool.Pop();
            if (b != null) Object.Destroy(b);
        }
    }

    #region Chunk Logic

    /// <summary>
    /// Retrieves a chunk from the pool or creates a new one if the pool is empty.
    /// Resets the chunk state for the new coordinate.
    /// </summary>
    public Chunk Get(ChunkCoord coord)
    {
        Chunk chunk;
        if (_chunkPool.Count > 0)
        {
            chunk = _chunkPool.Pop();
            chunk.Reset(coord);
        }
        else
        {
            // Constructor calls Reset internally
            chunk = new Chunk(coord);
        }

        _activeChunkCount++;
        return chunk;
    }

    /// <summary>
    /// Returns a chunk to the pool for reuse.
    /// </summary>
    public void Return(Chunk chunk)
    {
        if (chunk == null) return;

        chunk.Release(); // Unlink data, disable GameObject
        _chunkPool.Push(chunk);
        _activeChunkCount--;
    }

    #endregion

    #region Border Logic

    /// <summary>
    /// Retrieves a Border GameObject from the pool or instantiates a new one.
    /// </summary>
    public GameObject GetBorder(GameObject prefab, Vector3 position, Transform parent)
    {
        GameObject border;
        if (_borderPool.Count > 0)
        {
            border = _borderPool.Pop();
            border.transform.SetParent(parent);
            border.transform.position = position;
            border.SetActive(true);
        }
        else
        {
            border = Object.Instantiate(prefab, position, Quaternion.identity, parent);
        }

        // Update name for clarity
        ChunkCoord coord = new ChunkCoord(position);
        border.name = $"Border {coord.X}, {coord.Z}";

        return border;
    }

    public void ReturnBorder(GameObject border)
    {
        if (border == null) return;

        border.SetActive(false); // Disable before pooling
        _borderPool.Push(border);
    }

    #endregion

    /// <summary>
    /// Destroys all pooled chunks and cleans up resources.
    /// Call this on World destroy.
    /// </summary>
    public void Clear()
    {
        // Clear Chunks
        while (_chunkPool.Count > 0)
        {
            Chunk c = _chunkPool.Pop();
            c.Destroy();
        }

        // Clear Borders
        while (_borderPool.Count > 0)
        {
            GameObject b = _borderPool.Pop();
            if (b != null) Object.Destroy(b);
        }

        _activeChunkCount = 0;
        Debug.Log("[ChunkPoolManager] All Chunks in the ChunkPool have been disposed of..");
    }
}
