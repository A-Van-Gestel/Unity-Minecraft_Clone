using System.Collections.Generic;
using UnityEngine;

public class ChunkPoolManager
{
    private readonly Transform _worldParent;
    private readonly Stack<Chunk> _pool = new Stack<Chunk>();

    // Track statistics for debugging
    public int ActiveCount => _activeCount;
    public int PooledCount => _pool.Count;
    private int _activeCount = 0;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChunkPoolManager"/> class.
    /// </summary>
    /// <param name="worldTransform">The main World transform component where the new chunks will be created under.</param>
    public ChunkPoolManager(Transform worldTransform)
    {
        _worldParent = worldTransform;
    }

    /// <summary>
    /// Retrieves a chunk from the pool or creates a new one if the pool is empty.
    /// Resets the chunk state for the new coordinate.
    /// </summary>
    public Chunk Get(ChunkCoord coord)
    {
        Chunk chunk;
        if (_pool.Count > 0)
        {
            chunk = _pool.Pop();
            chunk.Reset(coord);
        }
        else
        {
            // Constructor calls Reset internally
            chunk = new Chunk(coord);
        }

        _activeCount++;
        return chunk;
    }

    /// <summary>
    /// Returns a chunk to the pool for reuse.
    /// </summary>
    public void Return(Chunk chunk)
    {
        if (chunk == null) return;

        chunk.Release(); // Unlink data, disable GameObject
        _pool.Push(chunk);
        _activeCount--;
    }

    /// <summary>
    /// Destroys all pooled chunks and cleans up resources.
    /// Call this on World destroy.
    /// </summary>
    public void Clear()
    {
        while (_pool.Count > 0)
        {
            Chunk c = _pool.Pop();
            c.Destroy();
        }

        _activeCount = 0;
        Debug.Log("[ChunkPoolManager] All Chunks in the ChunkPool have been disposed of..");
    }
}
