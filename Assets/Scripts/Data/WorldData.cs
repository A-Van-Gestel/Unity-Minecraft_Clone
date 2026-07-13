using System;
using System.Collections.Generic;
using Helpers;
using JetBrains.Annotations;
using Jobs;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Pool;

namespace Data
{
    [Serializable]
    public class WorldData
    {
        [MyBox.ReadOnly]
        public string worldName;

        [MyBox.ReadOnly]
        public int seed;

        [MyBox.ReadOnly]
        public long creationDate;

        [NonSerialized]
        public Dictionary<Vector2Int, ChunkData> Chunks = new Dictionary<Vector2Int, ChunkData>();

        [NonSerialized]
        public HashSet<ChunkData> ModifiedChunks = new HashSet<ChunkData>();

        [NonSerialized]
        public Dictionary<Vector2Int, HashSet<Vector2Int>> SunlightRecalculationQueue = new Dictionary<Vector2Int, HashSet<Vector2Int>>();

        // --- One-entry voxel-query cache (VQ-1) ---
        // Query bursts (an AABB collision scan, a ray march) overwhelmingly hit the same chunk, so caching the
        // last-resolved chunk turns the dictionary lookup into a Vector2Int compare. Main-thread only (every
        // GetVoxelState/TryGetVoxel caller is managed; jobs read gathered NativeArrays, not this path).
        // The cached ChunkData reference must not outlive its dictionary entry — a pool-recycled chunk at the
        // same key would serve stale data — so a topology version stamped on every Chunk's add/remove/clear
        // (see InvalidateVoxelQueryCache) fails the cache closed instead.
        [NonSerialized]
        private Vector2Int _cachedChunkKey;

        [NonSerialized]
        private ChunkData _cachedChunkData;

        [NonSerialized]
        private long _chunkTopologyVersion;

        [NonSerialized]
        private long _cachedTopologyVersion;

        #region Constructors

        /// <summary>
        /// Initializes a new instance of the <see cref="WorldData"/> class.
        /// </summary>
        /// <param name="worldName">The name of the world.</param>
        /// <param name="seed">The world generation seed.</param>
        public WorldData(string worldName, int seed)
        {
            this.worldName = worldName;
            this.seed = seed;
            creationDate = DateTime.UtcNow.Ticks;
        }

        /// <summary>
        /// Creates a copy of an existing <see cref="WorldData"/> instance.
        /// </summary>
        /// <param name="wD">The source WorldData.</param>
        public WorldData(WorldData wD)
        {
            worldName = wD.worldName;
            seed = wD.seed;
            creationDate = wD.creationDate;
        }

        #endregion

        #region Chunk Management

        /// <summary>
        /// Requests a chunk at the specified voxel-space world origin.
        /// </summary>
        /// <param name="chunkVoxelPos">The world origin of the chunk (X * ChunkWidth, Z * ChunkWidth).</param>
        /// <param name="allowChunkDataCreation">If true, a placeholder chunk will be created if it doesn't exist.</param>
        /// <returns>The ChunkData object if found or created; otherwise, null.</returns>
        public ChunkData RequestChunk(Vector2Int chunkVoxelPos, bool allowChunkDataCreation)
        {
            ChunkData c;

            if (Chunks.TryGetValue(chunkVoxelPos, out ChunkData chunk))
                c = chunk;
            else if (!allowChunkDataCreation)
                c = null;
            else
            {
                LoadChunk(chunkVoxelPos);
                c = Chunks[chunkVoxelPos];
            }

            return c;
        }

        /// <summary>
        /// Ensures a chunk is loaded into memory, either from disk or by creating a generation placeholder.
        /// </summary>
        /// <param name="chunkVoxelPos">The world origin of the chunk.</param>
        public void LoadChunk(Vector2Int chunkVoxelPos)
        {
            // Nothing needs to be loaded if the chunk is already loaded.
            if (Chunks.ContainsKey(chunkVoxelPos))
                return;

            // Load Chunk from File
            if (World.Instance.settings.EnablePersistence)
            {
                // PHASE 3 TODO-old: Replace the legacy save-system code below with ChunkStorageManager.LoadChunkAsync
                // TODO-new: This was the original place where chunks where loaded from disk, I believe this is the correct place (eg: data related), but is currently moved into World class itself.
                /*
                ChunkData chunk = SaveSystem.LoadChunk(worldName, chunkVector2Coord);
                if (chunk != null)
                {
                    Chunks.Add(chunkVector2Coord, chunk);
                    return;
                }
                */
            }

            // Chunk doesn't exist on disk (or loading is disabled/not yet implemented).
            // We create a "placeholder" ChunkData object.
            // The asynchronous job system is responsible for populating it.
            Chunks.Add(chunkVoxelPos, World.Instance.ChunkPool.GetChunkData(chunkVoxelPos)); // Create placeholder using POOL
            InvalidateVoxelQueryCache();
        }

        /// <summary>
        /// Invalidates the one-entry voxel-query cache used by <see cref="TryGetVoxel"/>. MUST be called after any
        /// structural change to <see cref="Chunks"/> (add, remove, or clear), so a cached <see cref="ChunkData"/>
        /// reference can never outlive its dictionary entry and serve data from a pool-recycled chunk at the same key.
        /// </summary>
        public void InvalidateVoxelQueryCache() => _chunkTopologyVersion++;


        /// <summary>
        /// This method is called by a modification that needs a chunk which may not exist yet.
        /// We can't populate it here, but we can make sure the placeholder exists so the mod can be queued.
        /// </summary>
        /// <param name="worldPos">The world position</param>
        /// <returns>Boolean representing if chunk already existed (TRUE), or if a placeholder was created (FALSE) or outside the world (FALSE)</returns>
        public bool EnsureChunkExists(Vector3 worldPos)
        {
            // Outside the world, nothing to do.
            if (!IsVoxelInWorld(worldPos)) return false;

            Vector2Int chunkVoxelPos = GetChunkCoordFor(worldPos);
            if (!Chunks.ContainsKey(chunkVoxelPos))
            {
                // Create the placeholder
                Chunks.Add(chunkVoxelPos, World.Instance.ChunkPool.GetChunkData(chunkVoxelPos)); // Create placeholder using POOL
                InvalidateVoxelQueryCache();
                return false;
            }

            return true;
        }

        /// <summary>
        /// Calculates the voxel-space world origin of the chunk containing the given world position.
        /// <para>Example: <c>Vector3(20, 0, 20)</c> -> <c>Vector2Int(16, 16)</c> (if ChunkWidth = 16)</para>
        /// </summary>
        /// <param name="worldPos">The world position.</param>
        /// <returns>The chunk's voxel-space origin.</returns>
        public Vector2Int GetChunkCoordFor(Vector3 worldPos)
        {
            int x = ChunkMath.WorldToChunk(worldPos.x) * VoxelData.ChunkWidth;
            int z = ChunkMath.WorldToChunk(worldPos.z) * VoxelData.ChunkWidth;
            return new Vector2Int(x, z);
        }

        /// <summary>
        /// Calculates the local voxel position within a chunk for a given world position.
        /// <para>Example: <c>Vector3(20.5f, 10f, 5f)</c> -> <c>Vector3Int(4, 10, 5)</c> (if ChunkWidth = 16)</para>
        /// </summary>
        /// <param name="worldPos">The world position.</param>
        /// <returns>The local (0-15) voxel position.</returns>
        public Vector3Int GetLocalVoxelPositionInChunk(Vector3 worldPos)
        {
            Vector2Int chunkVoxelPos = GetChunkCoordFor(worldPos);
            return new Vector3Int(
                Mathf.FloorToInt(worldPos.x - chunkVoxelPos.x),
                Mathf.FloorToInt(worldPos.y),
                Mathf.FloorToInt(worldPos.z - chunkVoxelPos.y));
        }

        #endregion

        #region Voxel Management

        /// <summary>
        /// Checks if a voxel is within the world bounds.
        /// </summary>
        /// <param name="worldPos">The world position</param>
        /// <returns>True if the voxel is within the world bounds, false otherwise.</returns>
        public bool IsVoxelInWorld(Vector3 worldPos)
        {
            // WS-3: XZ is fully unbounded — the west/south floor is gone too, so only the Y bound gates a voxel
            // (Tier A owns height). Negative XZ is now in-world; "out-of-world" in XZ no longer exists.
            return worldPos.y is >= 0 and < VoxelData.ChunkHeight;
        }

        /// <summary>
        /// Integer voxel-query fast path (VQ-1): resolves the voxel at an integer world coordinate with one
        /// chunk-coord computation (WS-1 shift/mask helpers), no float round-trip, and no nullable wrap. Backed by
        /// the one-entry last-chunk cache so a burst of queries into the same chunk skips the dictionary lookup.
        /// Prefer this over <see cref="GetVoxelState(Vector3)"/> for callers that already hold integer coordinates.
        /// </summary>
        /// <param name="x">World voxel X.</param>
        /// <param name="y">World voxel Y.</param>
        /// <param name="z">World voxel Z.</param>
        /// <param name="state">The resolved voxel state; <c>default</c> when the method returns false.</param>
        /// <returns>True when the coordinate is in-world and its chunk is loaded; false otherwise (matches the old <c>null</c>).</returns>
        public bool TryGetVoxel(int x, int y, int z, out VoxelState state)
        {
            // Integer world-bounds check. WS-3: XZ is fully unbounded (no floor), so only the Y bound remains —
            // the folded `(uint)y >= ChunkHeight` catches both y < 0 and y >= ChunkHeight in one test. Any XZ
            // (negative or positive) is in-world; resolution then depends purely on whether its chunk is loaded.
            if ((uint)y >= VoxelData.ChunkHeight)
            {
                state = default;
                return false;
            }

            // One chunk-coord computation, keyed by the chunk's voxel-space origin exactly like `Chunks`.
            Vector2Int chunkKey = new Vector2Int(
                ChunkMath.VoxelToChunk(x) * VoxelData.ChunkWidth,
                ChunkMath.VoxelToChunk(z) * VoxelData.ChunkWidth);

            ChunkData chunkData;
            if (_cachedChunkData != null && _cachedTopologyVersion == _chunkTopologyVersion && _cachedChunkKey == chunkKey)
            {
                chunkData = _cachedChunkData;
            }
            else if (Chunks.TryGetValue(chunkKey, out chunkData))
            {
                _cachedChunkKey = chunkKey;
                _cachedChunkData = chunkData;
                _cachedTopologyVersion = _chunkTopologyVersion;
            }
            else
            {
                state = default;
                return false;
            }

            state = new VoxelState(chunkData.GetVoxel(ChunkMath.VoxelToLocal(x), y, ChunkMath.VoxelToLocal(z)));
            return true;
        }

        /// <summary>
        /// Gets the voxel state at the given world position. Floor-then-delegate wrapper over the integer
        /// <see cref="TryGetVoxel"/> fast path.
        /// </summary>
        /// <param name="worldPos">The world position</param>
        /// <returns>The `voxel state` at the given position or `null` if the voxel is `outside the world` or the `chunk doesn't exist`.</returns>
        [CanBeNull]
        public VoxelState? GetVoxelState(Vector3 worldPos)
        {
            // If the voxel is outside the world, we don't need to do anything with it and return null.
            if (!IsVoxelInWorld(worldPos))
                return null;

            if (TryGetVoxel(Mathf.FloorToInt(worldPos.x), Mathf.FloorToInt(worldPos.y), Mathf.FloorToInt(worldPos.z),
                    out VoxelState state))
                return state;

            return null;
        }

        /// <summary>
        /// Queues a mesh rebuild for the given chunk.
        /// </summary>
        /// <param name="chunkVoxelPos">The global chunk coordinates of the given chunk</param>
        private void QueueMeshRebuild(Vector2Int chunkVoxelPos)
        {
            // Try to get the chunk's data.
            if (Chunks.TryGetValue(chunkVoxelPos, out ChunkData chunkData))
            {
                // If the chunk object exists, request a rebuild.
                if (chunkData.Chunk != null)
                {
                    World.Instance.RequestChunkMeshRebuild(chunkData.Chunk, true);
                }
            }
        }

        /// <summary>
        /// Helper method to get the raw voxel map for jobs.
        /// </summary>
        /// <param name="chunkVoxelPos">The global chunk coordinates of the given chunk</param>
        /// <param name="allocator">The allocator to use for the native array</param>
        /// <returns>Jobs compatible array of voxels</returns>
        public NativeArray<uint> GetChunkMapForJob(Vector2Int chunkVoxelPos, Allocator allocator)
        {
            // UninitializedMemory is safe: FillChunkMapForJob writes every element.
            NativeArray<uint> jobArray = new NativeArray<uint>(
                VoxelData.ChunkWidth * VoxelData.ChunkHeight * VoxelData.ChunkWidth,
                allocator, NativeArrayOptions.UninitializedMemory);

            FillChunkMapForJob(chunkVoxelPos, jobArray);
            return jobArray;
        }

        /// <summary>
        /// Fills a caller-provided full-chunk buffer with the raw voxel map for jobs.
        /// Writes EVERY element of <paramref name="jobArray"/> (null sections are zero-filled),
        /// so it is safe to use with stale pooled buffers from <see cref="Helpers.ChunkJobArrayPool"/>.
        /// </summary>
        /// <param name="chunkVoxelPos">The global chunk coordinates of the given chunk</param>
        /// <param name="jobArray">A buffer of length ChunkWidth × ChunkHeight × ChunkWidth to fill.</param>
        public void FillChunkMapForJob(Vector2Int chunkVoxelPos, NativeArray<uint> jobArray)
        {
            ChunkData chunk = RequestChunk(chunkVoxelPos, false);
            if (chunk != null)
                chunk.FillJobVoxelMap(jobArray);
            else
                ChunkData.FillEmptyVoxelMap(jobArray);
        }

        /// <summary>
        /// Creates a flat NativeArray copy of the chunk's ushort light data for Burst job processing.
        /// </summary>
        public NativeArray<ushort> GetChunkLightMapForJob(Vector2Int chunkVoxelPos, Allocator allocator)
        {
            // UninitializedMemory is safe: FillChunkLightMapForJob writes every element.
            NativeArray<ushort> jobArray = new NativeArray<ushort>(
                VoxelData.ChunkWidth * VoxelData.ChunkHeight * VoxelData.ChunkWidth,
                allocator, NativeArrayOptions.UninitializedMemory);

            FillChunkLightMapForJob(chunkVoxelPos, jobArray);
            return jobArray;
        }

        /// <summary>
        /// Fills a caller-provided full-chunk buffer with the chunk's ushort light data for jobs.
        /// Writes EVERY element of <paramref name="jobArray"/> (null sections are zero-filled),
        /// so it is safe to use with stale pooled buffers from <see cref="Helpers.ChunkJobArrayPool"/>.
        /// </summary>
        /// <param name="chunkVoxelPos">The global chunk coordinates of the given chunk</param>
        /// <param name="jobArray">A buffer of length ChunkWidth × ChunkHeight × ChunkWidth to fill.</param>
        public void FillChunkLightMapForJob(Vector2Int chunkVoxelPos, NativeArray<ushort> jobArray)
        {
            ChunkData chunk = RequestChunk(chunkVoxelPos, false);
            if (chunk != null)
                chunk.FillJobLightMap(jobArray);
            else
                ChunkData.FillEmptyLightMap(jobArray);

            if (World.Instance != null && !World.Instance.settings.enableLighting)
                LightingHelper.StampFullBrightSunlight(jobArray);
        }

        #endregion

        #region Lighting Management

        /// <summary>
        /// Queues a light update for the given voxel.
        /// </summary>
        /// <param name="worldPos">The world position of the voxel</param>
        /// <param name="oldLightLevel">The old light level of the voxel (Defaults to `0`)</param>
        /// <param name="channel">The light channel to update (Defaults to `Block Channel`)</param>
        public void QueueLightUpdate(Vector3 worldPos, byte oldLightLevel = 0, LightChannel channel = LightChannel.Block,
            byte oldBlockR = 0, byte oldBlockG = 0, byte oldBlockB = 0)
        {
            if (!IsVoxelInWorld(worldPos)) return;

            Vector2Int chunkVoxelPos = GetChunkCoordFor(worldPos);

            if (Chunks.TryGetValue(chunkVoxelPos, out ChunkData chunkData) && chunkData.IsPopulated)
            {
                Vector3Int localVoxelPos = GetLocalVoxelPositionInChunk(worldPos);
                if (channel == LightChannel.Block)
                    chunkData.AddToBlockLightQueue(localVoxelPos, oldLightLevel, oldBlockR, oldBlockG, oldBlockB);
                else
                    chunkData.AddToSunLightQueue(localVoxelPos, oldLightLevel);

                // Mark the target chunk as needing a lighting update.
                chunkData.HasLightChangesToProcess = true;
            }
            else
            {
                // If chunk is unloaded, tell ModManager to mark this area as dirty.
                // We don't have exact block tracking for unloaded chunks, so we mark the *Column* for recalculation.
                ChunkCoord chunkCoord = ChunkCoord.FromVoxelOrigin(chunkVoxelPos);

                // Calculate local column (0-15)
                Vector3Int localVoxelPos = GetLocalVoxelPositionInChunk(worldPos);
                Vector2Int localCol = new Vector2Int(localVoxelPos.x, localVoxelPos.z);

                // OPTIMIZATION: Use pool for the temporary set passed to AddPending
                HashSet<Vector2Int> tempSet = HashSetPool<Vector2Int>.Get();
                tempSet.Add(localCol);

                // Add to persistent manager
                World.Instance.LightingStateManager.AddPending(chunkCoord, tempSet);

                // AddPending copies the elements into its own set, so we can immediately release this temp set
                HashSetPool<Vector2Int>.Release(tempSet);
            }
        }

        /// <summary>
        /// Queues a sunlight recalculation for the given column.
        /// </summary>
        /// <param name="columnPos">The column position</param>
        public void QueueSunlightRecalculation(Vector2Int columnPos)
        {
            Vector2Int chunkVoxelPos = GetChunkCoordFor(new Vector3(columnPos.x, 0, columnPos.y));

            // OPTIMIZATION: Grab from the global pool
            if (!SunlightRecalculationQueue.TryGetValue(chunkVoxelPos, out HashSet<Vector2Int> columns))
            {
                columns = HashSetPool<Vector2Int>.Get();
                SunlightRecalculationQueue[chunkVoxelPos] = columns;
            }

            columns.Add(columnPos);

            // Mark the target chunk as needing a lighting update.
            if (Chunks.TryGetValue(chunkVoxelPos, out ChunkData chunkData))
            {
                chunkData.HasLightChangesToProcess = true;
            }
        }

        #endregion
    }
}
