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

        // Private backing for the chunk map: every structural mutation goes through SetChunk / RemoveChunk /
        // ClearChunks, so the VQ-1 topology-version bump can never be forgotten at a call site — the
        // compile-enforced form of the old "pair every mutation with InvalidateVoxelQueryCache" convention.
        [NonSerialized]
        private readonly Dictionary<Vector2Int, ChunkData> _chunks = new Dictionary<Vector2Int, ChunkData>();

        /// <summary>
        /// Read-only view of the loaded chunk map, keyed by chunk voxel-space origin (see <c>ChunkCoord</c>'s
        /// scale reference). Mutations go through <see cref="SetChunk"/> / <see cref="RemoveChunk"/> /
        /// <see cref="ClearChunks"/>, which keep the VQ-1 voxel-query cache coherent. Hot paths prefer
        /// <see cref="TryGetChunk"/> / <see cref="ChunkValues"/> / <see cref="ChunkKeys"/> — direct,
        /// struct-enumerator access with no interface dispatch or enumerator boxing.
        /// </summary>
        public IReadOnlyDictionary<Vector2Int, ChunkData> Chunks => _chunks;

        /// <summary>The number of loaded chunks.</summary>
        public int ChunkCount => _chunks.Count;

        /// <summary>The loaded chunks' data, with struct enumeration (GC-free <c>foreach</c> on hot paths).</summary>
        public Dictionary<Vector2Int, ChunkData>.ValueCollection ChunkValues => _chunks.Values;

        /// <summary>The loaded chunks' voxel-space origins, with struct enumeration (GC-free <c>foreach</c>).</summary>
        public Dictionary<Vector2Int, ChunkData>.KeyCollection ChunkKeys => _chunks.Keys;

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
        /// <param name="allowChunkDataCreation">If true, a placeholder chunk is created (via
        /// <see cref="GetOrCreatePlaceholder"/>) when the coord has no loaded chunk.
        /// ⚠️ Resurrect semantics: for a coord whose chunk was <b>unloaded</b>, this creates a fresh,
        /// UNPOPULATED placeholder — it does not restore the unloaded chunk's data (disk restore is the
        /// load pipeline's job). Callers that must not silently resurrect a placeholder pass false.
        /// The pipeline's create-true callers are safe by construction: <c>ProcessGenerationJobs</c>
        /// (its tracked job pins the placeholder against unload) and the <c>Chunk</c> visual re-link
        /// (<c>CheckViewDistance</c>'s spiral created every load-distance placeholder earlier in the same
        /// call) — both editor-asserted at the call site; <c>GetHighestVoxel</c> relies on creation.</param>
        /// <returns>The ChunkData object if found or created; otherwise, null.</returns>
        public ChunkData RequestChunk(Vector2Int chunkVoxelPos, bool allowChunkDataCreation)
        {
            if (allowChunkDataCreation)
                return GetOrCreatePlaceholder(chunkVoxelPos);

            return _chunks.TryGetValue(chunkVoxelPos, out ChunkData chunk) ? chunk : null;
        }

        /// <summary>Direct, non-virtual chunk lookup by voxel-space origin — the hot-path read.</summary>
        /// <param name="chunkVoxelPos">The chunk's voxel-space origin.</param>
        /// <param name="chunkData">The loaded chunk data, or null when not loaded.</param>
        /// <returns>True when the chunk is loaded.</returns>
        public bool TryGetChunk(Vector2Int chunkVoxelPos, out ChunkData chunkData) =>
            _chunks.TryGetValue(chunkVoxelPos, out chunkData);

        /// <summary>Adds or replaces the chunk at a voxel-space origin, keeping the voxel-query cache coherent.</summary>
        /// <param name="chunkVoxelPos">The chunk's voxel-space origin.</param>
        /// <param name="chunkData">The chunk data to register.</param>
        public void SetChunk(Vector2Int chunkVoxelPos, ChunkData chunkData)
        {
            _chunks[chunkVoxelPos] = chunkData;
            InvalidateVoxelQueryCache();
        }

        /// <summary>Removes the chunk at a voxel-space origin, keeping the voxel-query cache coherent.</summary>
        /// <param name="chunkVoxelPos">The chunk's voxel-space origin.</param>
        /// <returns>True when a chunk was removed.</returns>
        public bool RemoveChunk(Vector2Int chunkVoxelPos)
        {
            bool removed = _chunks.Remove(chunkVoxelPos);
            if (removed) InvalidateVoxelQueryCache();
            return removed;
        }

        /// <summary>Removes every loaded chunk, keeping the voxel-query cache coherent.</summary>
        public void ClearChunks()
        {
            _chunks.Clear();
            InvalidateVoxelQueryCache();
        }

        /// <summary>
        /// Invalidates the one-entry voxel-query cache used by <see cref="TryGetVoxel"/>. Called by the chunk-map
        /// mutators above — the only code that can structurally change <see cref="Chunks"/> — so a cached
        /// <see cref="ChunkData"/> reference can never outlive its dictionary entry and serve data from a
        /// pool-recycled chunk at the same key.
        /// </summary>
        private void InvalidateVoxelQueryCache() => _chunkTopologyVersion++;


        /// <summary>
        /// Gets the chunk at the given voxel-space origin, creating a pooled placeholder when absent —
        /// the single placeholder-creation site (CP-4 / F2). Creation stays on
        /// <see cref="ChunkPoolManager.GetChunkData"/> so the pool's <c>Reset</c> runs (bumping
        /// <c>ChunkData.LifecycleEpoch</c> for pool-ABA detection). Deliberately does NOT set
        /// <c>IsLoading</c> or enqueue generation — admission in <c>World.DrainGenerationRequests</c>
        /// owns both (P-4 §3.1); the placeholder stays eligible until admitted.
        /// </summary>
        /// <param name="chunkVoxelPos">The chunk's voxel-space origin. Must be chunk-origin-aligned —
        /// unlike the retired <c>EnsureChunkExists</c> overloads, this method does NOT normalize
        /// arbitrary voxel positions (convert via <see cref="GetChunkCoordFor(Vector3Int)"/> first).</param>
        /// <returns>The existing chunk, or the freshly created unpopulated placeholder.</returns>
        public ChunkData GetOrCreatePlaceholder(Vector2Int chunkVoxelPos)
        {
#if UNITY_EDITOR
            // A misaligned key would register a phantom chunk no origin-based lookup can ever find —
            // assert alignment (sign-safe for both signs via the sanctioned ChunkMath helper).
            Debug.Assert(ChunkMath.IsChunkAligned(chunkVoxelPos.x) && ChunkMath.IsChunkAligned(chunkVoxelPos.y),
                "GetOrCreatePlaceholder: position is not a chunk origin — normalize via GetChunkCoordFor first.");
#endif
            if (_chunks.TryGetValue(chunkVoxelPos, out ChunkData chunkData))
                return chunkData;

            ChunkData placeholder = World.Instance.ChunkPool.GetChunkData(chunkVoxelPos);
            SetChunk(chunkVoxelPos, placeholder);
            return placeholder;
        }

        /// <summary>
        /// Calculates the voxel-space world origin of the chunk containing the given world position.
        /// <para>Example: <c>Vector3(20, 0, 20)</c> -> <c>Vector2Int(16, 16)</c> (if ChunkWidth = 16)</para>
        /// <para>⚠️ Float precision caps at ±2²⁴: integer voxel sources (e.g. <c>Vector3Int</c>
        /// positions) must use the <see cref="GetChunkCoordFor(Vector3Int)"/> overload, which is exact
        /// to the ±2³¹ edge — an implicit int→float conversion here silently mis-chunks far
        /// coordinates (Bug 19).</para>
        /// </summary>
        /// <param name="worldPos">The world position.</param>
        /// <returns>The chunk's voxel-space origin.</returns>
        public Vector2Int GetChunkCoordFor(Vector3 worldPos)
        {
            AssertWithinFloatPrecision(worldPos.x, worldPos.z);
            int x = ChunkMath.WorldToChunk(worldPos.x) * VoxelData.ChunkWidth;
            int z = ChunkMath.WorldToChunk(worldPos.z) * VoxelData.ChunkWidth;
            return new Vector2Int(x, z);
        }

        /// <summary>
        /// Integer-exact overload: the voxel-space world origin of the chunk containing the given
        /// voxel cell. Pure shift math — exact to the ±2³¹ edge at any sign.
        /// </summary>
        /// <param name="voxelCell">The global voxel cell.</param>
        /// <returns>The chunk's voxel-space origin.</returns>
        public Vector2Int GetChunkCoordFor(Vector3Int voxelCell)
        {
            return new Vector2Int(
                ChunkMath.VoxelToChunk(voxelCell.x) * VoxelData.ChunkWidth,
                ChunkMath.VoxelToChunk(voxelCell.z) * VoxelData.ChunkWidth);
        }

        /// <summary>
        /// Calculates the local voxel position within a chunk for a given world position.
        /// <para>Example: <c>Vector3(20.5f, 10f, 5f)</c> -> <c>Vector3Int(4, 10, 5)</c> (if ChunkWidth = 16)</para>
        /// <para>⚠️ Float precision caps at ±2²⁴: integer voxel sources must use the
        /// <see cref="GetLocalVoxelPositionInChunk(Vector3Int)"/> overload (see
        /// <see cref="GetChunkCoordFor(Vector3)"/>).</para>
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

        /// <summary>
        /// Integer-exact overload: the chunk-local voxel position of the given global voxel cell.
        /// Pure mask math — exact to the ±2³¹ edge at any sign.
        /// </summary>
        /// <param name="voxelCell">The global voxel cell.</param>
        /// <returns>The local (0-15 on X/Z) voxel position; Y passes through unchanged.</returns>
        public Vector3Int GetLocalVoxelPositionInChunk(Vector3Int voxelCell)
        {
            return new Vector3Int(
                ChunkMath.VoxelToLocal(voxelCell.x),
                voxelCell.y,
                ChunkMath.VoxelToLocal(voxelCell.z));
        }

        /// <summary>
        /// Dev-build tripwire on the float query paths: an XZ magnitude at or beyond 2²⁴ has already
        /// lost integer precision in the float, so the caller is either a mis-typed integer source
        /// (route it through the <c>Vector3Int</c> overloads) or genuinely broken far-out float math.
        /// Latched so a far-out frame logs once instead of spamming per query.
        /// </summary>
        /// <param name="x">The world-position X.</param>
        /// <param name="z">The world-position Z.</param>
        [System.Diagnostics.Conditional("UNITY_EDITOR"), System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
        private static void AssertWithinFloatPrecision(float x, float z)
        {
            const float MAX_EXACT_INT_FLOAT = 1 << 24;
            if (s_floatPrecisionTripped || (Mathf.Abs(x) < MAX_EXACT_INT_FLOAT && Mathf.Abs(z) < MAX_EXACT_INT_FLOAT))
                return;

            s_floatPrecisionTripped = true;
            Debug.LogError($"[WorldData] Float-space chunk query at ({x}, {z}) exceeds float integer precision " +
                           "(±2^24) — integer voxel sources must use the Vector3Int overloads (Bug 19 class). " +
                           "Further occurrences suppressed this session.");
        }

        private static bool s_floatPrecisionTripped;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetFloatPrecisionTripwire() => s_floatPrecisionTripped = false;

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
            else if (_chunks.TryGetValue(chunkKey, out chunkData))
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
            if (_chunks.TryGetValue(chunkVoxelPos, out ChunkData chunkData))
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

            if (_chunks.TryGetValue(chunkVoxelPos, out ChunkData chunkData) && chunkData.IsPopulated)
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
            Vector2Int chunkVoxelPos = SunlightColumnRouting.RouteToChunkOrigin(columnPos);

            // OPTIMIZATION: Grab from the global pool
            if (!SunlightRecalculationQueue.TryGetValue(chunkVoxelPos, out HashSet<Vector2Int> columns))
            {
                columns = HashSetPool<Vector2Int>.Get();
                SunlightRecalculationQueue[chunkVoxelPos] = columns;
            }

            columns.Add(columnPos);

            // Mark the target chunk as needing a lighting update.
            if (_chunks.TryGetValue(chunkVoxelPos, out ChunkData chunkData))
            {
                chunkData.HasLightChangesToProcess = true;
            }
        }

        #endregion
    }
}
