using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Data;
using Helpers;
using UnityEngine;

namespace Serialization
{
    public class ChunkStorageManager
    {
        private readonly string _saveFolderPath;
        private readonly IRegionAddressCodec _codec;

        // CP-1 save-durability probe (F5 evidence). Static so the debug HUD can read them without a manager ref.
        // Interlocked: SaveChunkAsync's body resumes on a ThreadPool thread, so Completed/Failed cross threads.
        private static long s_savesFired;
        private static long s_savesCompleted;
        private static long s_savesFailed;

        /// <summary>Cumulative count of <see cref="SaveChunkAsync"/> invocations (CP-1 probe).</summary>
        public static long SavesFired => Interlocked.Read(ref s_savesFired);

        /// <summary>Cumulative count of async saves that reached the write without throwing (CP-1 probe).</summary>
        public static long SavesCompleted => Interlocked.Read(ref s_savesCompleted);

        /// <summary>Cumulative count of save attempts (initial or retry) that failed to reach disk (CP-1 probe; CP-6 routes each failure into the retry registry).</summary>
        public static long SavesFailed => Interlocked.Read(ref s_savesFailed);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        // CP-6 test seams: upcoming save write attempts that throw / serializations that return 0 bytes
        // (dev-only fault injection).
        private static int s_injectedSaveFaults;
        private static int s_injectedZeroLengthSerializes;

        /// <summary>Arms the dev-only save fault injection: the next <paramref name="count"/> save write
        /// attempts (initial, retry, or sync quit save — any thread) throw before touching disk. Used by
        /// the Validate Save Durability suite and manual F5 prove-red runs; compiled out of release builds.</summary>
        /// <param name="count">Number of consecutive attempts to fault (0 disarms).</param>
        public static void InjectSaveFaults(int count) => Interlocked.Exchange(ref s_injectedSaveFaults, count);

        /// <summary>Arms the dev-only zero-length serialization injection: the next <paramref name="count"/>
        /// chunk serializations report 0 bytes — the deterministic (non-retryable) failure shape that must
        /// take the <see cref="ChunkSaveResult.FailedPermanent"/> arm, never the retry loop.</summary>
        /// <param name="count">Number of consecutive serializations to zero out (0 disarms).</param>
        public static void InjectZeroLengthSerializes(int count) => Interlocked.Exchange(ref s_injectedZeroLengthSerializes, count);
#endif

        /// <summary>Throws when the dev-only save fault injection is armed (no-op in release builds).</summary>
        private static void ThrowIfInjectedSaveFault()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            int observed = Interlocked.Decrement(ref s_injectedSaveFaults);
            if (observed >= 0)
                throw new IOException("[CP-6 TEST] Injected save fault");

            // Clamp the disarmed counter back to 0 without racing a concurrent re-arm: only restore 0 if
            // the value is still the negative we produced (a fresh InjectSaveFaults(n) wins otherwise).
            Interlocked.CompareExchange(ref s_injectedSaveFaults, 0, observed);
#endif
        }

        /// <summary>Serializes a chunk into <paramref name="buffer"/>, honoring the dev-only zero-length
        /// injection seam (release builds compile to a plain <see cref="ChunkSerializer.Serialize"/> call).</summary>
        /// <param name="source">The chunk (live data or snapshot) to serialize.</param>
        /// <param name="buffer">The pooled destination buffer.</param>
        /// <param name="algorithm">The compression algorithm to apply.</param>
        /// <returns>The payload length, or 0 when the injection seam is armed.</returns>
        private static int SerializeWithInjection(ChunkData source, byte[] buffer, CompressionAlgorithm algorithm)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            int observed = Interlocked.Decrement(ref s_injectedZeroLengthSerializes);
            if (observed >= 0) return 0;
            Interlocked.CompareExchange(ref s_injectedZeroLengthSerializes, 0, observed);
#endif
            return ChunkSerializer.Serialize(source, buffer, algorithm);
        }

        /// <summary>Resets the CP-1 save counters on each play-mode entry (safe when domain reload is disabled).</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetSaveProbeCounters()
        {
            s_savesFired = 0;
            s_savesCompleted = 0;
            s_savesFailed = 0;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            s_injectedSaveFaults = 0;
            s_injectedZeroLengthSerializes = 0;
#endif
        }

        // Concurrent Dictionary with Lazy to ensure thread-safe, single initialization of RegionFiles
        private readonly ConcurrentDictionary<Vector2Int, Lazy<RegionFile>> _regions = new ConcurrentDictionary<Vector2Int, Lazy<RegionFile>>();

        /// <summary>
        /// Initializes a new instance of the ChunkStorageManager, setting up region file I/O for the specified world.
        /// </summary>
        /// <param name="worldName">Name of the world being loaded or created.</param>
        /// <param name="useVolatilePath">True in Editor mode to use a temp save directory.</param>
        /// <param name="saveVersion">
        /// The version field read from <c>level.dat</c>. Determines which
        /// <see cref="IRegionAddressCodec"/> is used for all region address arithmetic.
        /// Pass <see cref="SaveSystem.CURRENT_VERSION"/> for new worlds.
        /// </param>
        public ChunkStorageManager(string worldName, bool useVolatilePath, int saveVersion)
        {
            // Determine Save Path
            string worldPath = SaveSystem.GetSavePath(worldName, useVolatilePath);
            _saveFolderPath = Path.Combine(worldPath, "Region");
            _codec = RegionAddressCodec.ForVersion(saveVersion);

            if (!Directory.Exists(_saveFolderPath)) Directory.CreateDirectory(_saveFolderPath);
        }

        // -------------------------------------------------------------------------
        // Public API
        // -------------------------------------------------------------------------

        /// <summary>
        /// Asynchronously loads and deserializes a chunk from its corresponding region file into memory.
        /// I/O operations and decompression are kept on background threads to prevent frame drops.
        /// </summary>
        /// <param name="chunkVoxelPos">The voxel-space world origin of the chunk.</param>
        /// <returns>The deserialized <see cref="ChunkData"/>, or null if the chunk does not exist on disk.</returns>
        public async Task<ChunkData> LoadChunkAsync(Vector2Int chunkVoxelPos)
        {
            // CP-6 reload guard: land any pending failed-save retry for this coord first, so the read
            // below never returns pre-edit bytes. Runs synchronously on the caller (main) thread.
            FlushPendingRetryFor(chunkVoxelPos);

#if DEVELOPMENT_BUILD || UNITY_EDITOR
            bool logSaveDiagnostics = World.Instance.settings.enableSaveSystemDiagnosticLogs;
#endif
            // Run I/O on background thread
            return await Task.Run(() =>
            {
                (Vector2Int regionCoord, int lx, int lz) = _codec.ChunkVoxelPosToRegionAddress(chunkVoxelPos);
                RegionFile region = GetRegion(regionCoord);

                (byte[] data, CompressionAlgorithm algorithm) = region.LoadChunkData(lx, lz);
                if (data == null)
                {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                    if (logSaveDiagnostics)
                        Debug.Log($"[LoadChunkAsync] Chunk at voxelPos {chunkVoxelPos} not on disk -> Will be generated");
#endif
                    return null;
                }

                // Deserialize (Expensive CPU, kept on background thread)
                ChunkData chunk = ChunkSerializer.Deserialize(data, algorithm, chunkVoxelPos);

                if (chunk == null)
                {
                    Debug.LogWarning($"[LoadChunkAsync] Chunk at voxelPos {chunkVoxelPos} deserialization failed -> Will be (re-)generated");

                    return null;
                }

                return chunk;
            });
        }

        /// <summary>
        /// Synchronously serializes, compresses, and saves a chunk to its region file.
        /// This directly blocks the calling thread, rendering it suitable primarily for application shutdown logic.
        /// </summary>
        /// <param name="data">The chunk data object to persist.</param>
        public void SaveChunk(ChunkData data)
        {
            CompressionAlgorithm algorithm = World.Instance.settings.saveCompression;

            // Get buffer from pool to avoid GC allocation on main thread
            byte[] buffer = SerializationBufferPool.Get();
            try
            {
                // Serialize
                int length = SerializeWithInjection(data, buffer, algorithm);
                if (length <= 0)
                {
                    Debug.LogWarning($"[SaveChunk] Chunk at voxelPos {data.Position.ToString()} serialization returned 0 bytes");
                    return;
                }

                WriteToRegion(data.Position, buffer, length, algorithm);
            }
            catch (Exception e)
            {
                Debug.LogError($"[SaveChunk] Failed to save chunk at voxelPos {data.Position.ToString()}: {e.Message}");
            }
            finally
            {
                SerializationBufferPool.Return(buffer);
            }
        }

        /// <summary>
        /// Async save. Snapshots chunk data on the calling thread, then serializes and writes on a ThreadPool thread.
        /// Includes CancellationToken support to safely abort on game quit.
        /// On failure OR quit-cancellation the snapshot — the edits' only surviving copy once the live
        /// <see cref="ChunkData"/> is pool-recycled — is handed to the failed-save retry registry instead of
        /// the pool (CP-6 durability); deterministic failures are released and logged (never retried).
        /// </summary>
        /// <param name="data">The chunk data to save.</param>
        /// <param name="cancellationToken">An optional cancellation token to abort the task.</param>
        /// <returns>The save outcome; <see cref="ChunkSaveResult.Failed"/> means the retry registry now owns the edits.</returns>
        public async Task<ChunkSaveResult> SaveChunkAsync(ChunkData data, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref s_savesFired);

            // 1. Get Preferred Algorithm from Global Settings
            CompressionAlgorithm algorithm = World.Instance.settings.saveCompression;

            // 2. Get Buffer
            byte[] buffer = SerializationBufferPool.Get();

            // 3. Create a thread-safe snapshot on the Main Thread (Zero GC via Pooling)
            ChunkData snapshot = CreateSerializationSnapshot(data);

            ChunkSaveResult result;
            try
            {
                // Check token before expensive work
                if (cancellationToken.IsCancellationRequested)
                {
                    result = ChunkSaveResult.Canceled;
                }
                else
                {
                    ChunkSaveResult taskResult = ChunkSaveResult.Canceled;

                    // 4. Offload serialization of the isolated snapshot to Thread Pool
                    await Task.Run(() =>
                    {
                        // Serialize
                        int length = SerializeWithInjection(snapshot, buffer, algorithm);
                        if (length <= 0)
                        {
                            // Deterministic failure — retrying can never succeed, so this must NOT enter
                            // the retry loop (this path used to skip silently).
                            Interlocked.Increment(ref s_savesFailed);
                            Debug.LogError($"[SaveChunkAsync] Chunk at voxelPos {snapshot.Position.ToString()} serialization returned 0 bytes — edits to this chunk cannot be persisted.");
                            taskResult = ChunkSaveResult.FailedPermanent;
                            return;
                        }

                        // Check token again before disk write to prevent writing partial/canceled state
                        if (cancellationToken.IsCancellationRequested) return; // taskResult stays Canceled

                        WriteToRegion(snapshot.Position, buffer, length, algorithm);

                        // Count only a real disk write (skips the length<=0 / canceled early-returns above),
                        // so SavesCompleted reflects actual writes rather than every non-throwing invocation.
                        Interlocked.Increment(ref s_savesCompleted);
                        taskResult = ChunkSaveResult.Written;
                    }, cancellationToken);

                    result = taskResult;
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during quit — the synchronous quit-time save path owns final persistence.
                result = ChunkSaveResult.Canceled;
            }
            catch (Exception e)
            {
                Interlocked.Increment(ref s_savesFailed);
                Debug.LogError($"[SaveChunkAsync] Failed to save chunk at voxelPos {data.Position.ToString()}: {e.Message}");
                result = ChunkSaveResult.Failed;
            }
            finally
            {
                // Always return the buffer to the pool
                SerializationBufferPool.Return(buffer);
            }

            // Snapshot disposition — exactly once per path (any thread — staged entries are drained on the
            // main thread):
            //   Failed          → retry registry (a later attempt may land the write).
            //   Canceled        → retry registry too: cancel only comes from the quit token, and a canceled
            //                     save's chunk may already be gone from ModifiedChunks (unload site) or
            //                     cleared by a manual save — the quit-time FlushFailedSavesSync writes the
            //                     staged snapshot synchronously, so those edits are not silently lost.
            //   FailedPermanent → pool: deterministic failure, retrying is an infinite loop; loss already
            //                     logged loudly above.
            //   Written         → pool.
            if (result == ChunkSaveResult.Failed || result == ChunkSaveResult.Canceled)
                StageFailedSave(snapshot);
            else
                World.Instance.ChunkPool.ReturnChunkData(snapshot);

            return result;
        }

        /// <summary>
        /// Safely disposes all open region files, forcing their physical file streams to flush pending I/O bytes to the drive.
        /// Call this only when tearing down the storage manager.
        /// </summary>
        public void Dispose()
        {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            Debug.Log($"[ChunkStorageManager] Disposing {_regions.Count} region files...");
#endif
            foreach (Lazy<RegionFile> lazyRegion in _regions.Values)
            {
                // Only dispose if the file was actually opened
                if (lazyRegion.IsValueCreated)
                {
                    lazyRegion.Value.Dispose(); // This calls RegionFile.Dispose()
                }
            }

            _regions.Clear();
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            Debug.Log("[ChunkStorageManager] All regions disposed.");
#endif
        }

        // -------------------------------------------------------------------------
        // CP-6: failed-save retry registry
        // -------------------------------------------------------------------------
        // A failed save's snapshot is the only surviving copy of the chunk's edits (the live ChunkData is
        // pool-recycled immediately after the unload fires the save), so the registry takes ownership of
        // the snapshot and re-attempts the write until it lands or the quit-time flush makes the final
        // attempt. Staging is thread-safe (the async save body may complete off the main thread); the
        // registry map is touched only from the main thread (Update drain / load guard / quit flush).

        private const double RETRY_BACKOFF_BASE_SECONDS = 1.0;
        private const double RETRY_BACKOFF_MAX_SECONDS = 30.0;
        private const int RETRY_BACKOFF_MAX_DOUBLINGS = 5;

        /// <summary>A failed save awaiting retry; owns its serialization snapshot until the write lands.</summary>
        private sealed class FailedSaveEntry
        {
            public ChunkData Snapshot;
            public int Attempts;
            public double NextAttemptTime;
        }

        private readonly ConcurrentQueue<ChunkData> _failedSaveStaging = new ConcurrentQueue<ChunkData>();
        private readonly Dictionary<Vector2Int, FailedSaveEntry> _failedSaves = new Dictionary<Vector2Int, FailedSaveEntry>();

        /// <summary>Failed saves currently awaiting retry (staged + registered). Debug HUD readout.</summary>
        public int PendingFailedSaves => _failedSaveStaging.Count + _failedSaves.Count;

        /// <summary>Hands a failed save's snapshot to the retry registry (callable from any thread).</summary>
        /// <param name="snapshot">The serialization snapshot whose ownership transfers to the registry.</param>
        private void StageFailedSave(ChunkData snapshot) => _failedSaveStaging.Enqueue(snapshot);

        /// <summary>Moves staged failures into the main-thread registry map. On a duplicate coord the newer
        /// snapshot supersedes and the older one returns to the pool. Main thread only.</summary>
        private void DrainStagingIntoRegistry()
        {
            while (_failedSaveStaging.TryDequeue(out ChunkData snapshot))
            {
                if (_failedSaves.TryGetValue(snapshot.Position, out FailedSaveEntry existing))
                {
                    World.Instance.ChunkPool.ReturnChunkData(existing.Snapshot);
                    existing.Snapshot = snapshot;
                    existing.Attempts = 0;
                    existing.NextAttemptTime = 0d; // due immediately
                }
                else
                {
                    _failedSaves.Add(snapshot.Position, new FailedSaveEntry { Snapshot = snapshot });
                }
            }
        }

        /// <summary>
        /// Retries at most one due failed save per call (main-thread per-frame hook; cheap no-op when empty).
        /// Success returns the snapshot to the pool; failure backs off exponentially and stays registered —
        /// an entry is never dropped while the session lives (<see cref="FlushFailedSavesSync"/> makes the
        /// final attempt at quit).
        /// </summary>
        /// <param name="ignoreBackoff">True to retry regardless of the entries' backoff windows (tests).</param>
        /// <returns>The number of failed saves recovered (written) by this call: 0 or 1.</returns>
        public int DrainFailedSaveRetries(bool ignoreBackoff = false)
        {
            DrainStagingIntoRegistry();
            if (_failedSaves.Count == 0) return 0;

            double now = Time.realtimeSinceStartupAsDouble;
            Vector2Int dueCoord = default;
            FailedSaveEntry due = null;
            foreach (KeyValuePair<Vector2Int, FailedSaveEntry> kvp in _failedSaves)
            {
                if (!ignoreBackoff && kvp.Value.NextAttemptTime > now) continue;
                dueCoord = kvp.Key;
                due = kvp.Value;
                break;
            }

            if (due == null) return 0;

            ChunkSaveResult attempt = WriteSnapshot(due.Snapshot);
            if (attempt == ChunkSaveResult.Written)
            {
                Debug.Log($"[SaveRetry] Recovered failed save for chunk at voxelPos {dueCoord.ToString()} after {(due.Attempts + 1).ToString()} attempt(s).");
                World.Instance.ChunkPool.ReturnChunkData(due.Snapshot);
                _failedSaves.Remove(dueCoord);
                return 1;
            }

            if (attempt == ChunkSaveResult.FailedPermanent)
            {
                // Deterministic failure — keeping the entry would retry forever. Drop loudly.
                Debug.LogError($"[SaveRetry] Dropping unrecoverable save for chunk at voxelPos {dueCoord.ToString()} — this session's edits to that chunk are lost.");
                World.Instance.ChunkPool.ReturnChunkData(due.Snapshot);
                _failedSaves.Remove(dueCoord);
                return 0;
            }

            due.Attempts++;
            double backoff = Math.Min(
                RETRY_BACKOFF_BASE_SECONDS * (1 << Math.Min(due.Attempts, RETRY_BACKOFF_MAX_DOUBLINGS)),
                RETRY_BACKOFF_MAX_SECONDS);
            due.NextAttemptTime = now + backoff;
            Debug.LogError($"[SaveRetry] Retry {due.Attempts.ToString()} failed for chunk at voxelPos {dueCoord.ToString()} — next attempt in {backoff.ToString("F0")}s. Edits are retained in memory.");
            return 0;
        }

        /// <summary>
        /// Synchronous flush of every pending failed save (quit / force-unload path — call before
        /// <see cref="Dispose"/>). One attempt each. Recovered and unrecoverable (deterministic) entries
        /// are removed; a retryably-failing entry is RETAINED — at quit that is moot (process ends), but a
        /// force-unload keeps the session alive and the per-frame drain can still recover it later.
        /// </summary>
        /// <returns>The number of failed saves recovered (written).</returns>
        public int FlushFailedSavesSync()
        {
            DrainStagingIntoRegistry();
            if (_failedSaves.Count == 0) return 0;

            int recovered = 0;
            List<Vector2Int> coords = new List<Vector2Int>(_failedSaves.Keys); // rare path; removal-safe iteration
            foreach (Vector2Int coord in coords)
            {
                FailedSaveEntry entry = _failedSaves[coord];
                ChunkSaveResult attempt = WriteSnapshot(entry.Snapshot);
                if (attempt == ChunkSaveResult.Written)
                {
                    recovered++;
                }
                else if (attempt == ChunkSaveResult.FailedPermanent)
                {
                    Debug.LogError($"[SaveRetry] Final flush: unrecoverable save for chunk at voxelPos {coord.ToString()} — this session's edits to that chunk are lost.");
                }
                else
                {
                    // Retryable — keep the entry (and its snapshot) for the per-frame drain / a later flush.
                    Debug.LogError($"[SaveRetry] Final flush failed for chunk at voxelPos {coord.ToString()} — entry retained for retry.");
                    continue;
                }

                World.Instance.ChunkPool.ReturnChunkData(entry.Snapshot);
                _failedSaves.Remove(coord);
            }

            return recovered;
        }

        /// <summary>
        /// CP-6 reload guard: if a failed save for this coord is still pending, complete it synchronously
        /// before the chunk is loaded, so the load never reads pre-edit bytes. Main thread only (runs in
        /// <see cref="LoadChunkAsync"/>'s synchronous prefix, before the background hand-off).
        /// </summary>
        /// <param name="chunkVoxelPos">The voxel-space world origin of the chunk about to load.</param>
        private void FlushPendingRetryFor(Vector2Int chunkVoxelPos)
        {
            // Known window: this guard only sees saves that have already FAILED and staged. A save still
            // in flight for this coord (unload → immediate re-entry, milliseconds) is invisible here, and
            // the load may read pre-save bytes — closing that needs per-coord in-flight save tracking
            // (deliberately not built; see CP doc §7 CP-6).
            DrainStagingIntoRegistry();
            if (!_failedSaves.TryGetValue(chunkVoxelPos, out FailedSaveEntry entry)) return;

            ChunkSaveResult attempt = WriteSnapshot(entry.Snapshot);
            if (attempt == ChunkSaveResult.Written)
            {
                Debug.Log($"[SaveRetry] Reload guard flushed pending save for chunk at voxelPos {chunkVoxelPos.ToString()} before load.");
                World.Instance.ChunkPool.ReturnChunkData(entry.Snapshot);
                _failedSaves.Remove(chunkVoxelPos);
            }
            else if (attempt == ChunkSaveResult.FailedPermanent)
            {
                Debug.LogError($"[SaveRetry] Reload guard: unrecoverable save for chunk at voxelPos {chunkVoxelPos.ToString()} — dropping; loading last-persisted data.");
                World.Instance.ChunkPool.ReturnChunkData(entry.Snapshot);
                _failedSaves.Remove(chunkVoxelPos);
            }
            else
            {
                // Keep the entry (the periodic retry continues); this load reads stale bytes until it lands.
                Debug.LogError($"[SaveRetry] Reload guard could NOT flush pending save for chunk at voxelPos {chunkVoxelPos.ToString()} — loading stale data; retry stays registered.");
            }
        }

        /// <summary>Serializes and writes an owned snapshot on the calling thread (retry/flush path).
        /// Does not dispose the snapshot — the caller owns its disposition.</summary>
        /// <param name="snapshot">The registry-owned serialization snapshot to persist.</param>
        /// <returns><see cref="ChunkSaveResult.Written"/>, <see cref="ChunkSaveResult.Failed"/> (retryable),
        /// or <see cref="ChunkSaveResult.FailedPermanent"/> (deterministic — caller must drop the entry).</returns>
        private ChunkSaveResult WriteSnapshot(ChunkData snapshot)
        {
            CompressionAlgorithm algorithm = World.Instance.settings.saveCompression;
            byte[] buffer = SerializationBufferPool.Get();
            try
            {
                int length = SerializeWithInjection(snapshot, buffer, algorithm);
                if (length <= 0)
                {
                    Interlocked.Increment(ref s_savesFailed);
                    Debug.LogError($"[SaveRetry] Chunk at voxelPos {snapshot.Position.ToString()} serialization returned 0 bytes — not retryable.");
                    return ChunkSaveResult.FailedPermanent;
                }

                WriteToRegion(snapshot.Position, buffer, length, algorithm);

                Interlocked.Increment(ref s_savesCompleted);
                return ChunkSaveResult.Written;
            }
            catch (Exception e)
            {
                Interlocked.Increment(ref s_savesFailed);
                Debug.LogError($"[SaveRetry] Write failed for chunk at voxelPos {snapshot.Position.ToString()}: {e.Message}");
                return ChunkSaveResult.Failed;
            }
            finally
            {
                SerializationBufferPool.Return(buffer);
            }
        }

        /// <summary>Resolves the chunk's region address and writes the serialized payload — the single
        /// write core shared by the sync, async, and retry save paths (and the one site the dev-only
        /// fault seam hooks, so every path is injectable).</summary>
        /// <param name="chunkVoxelPos">The chunk's voxel-space world origin.</param>
        /// <param name="buffer">The serialized payload buffer.</param>
        /// <param name="length">The payload length in bytes.</param>
        /// <param name="algorithm">The compression algorithm the payload was written with.</param>
        private void WriteToRegion(Vector2Int chunkVoxelPos, byte[] buffer, int length, CompressionAlgorithm algorithm)
        {
            ThrowIfInjectedSaveFault();

            (Vector2Int regionCoord, int lx, int lz) = _codec.ChunkVoxelPosToRegionAddress(chunkVoxelPos);
            GetRegion(regionCoord).SaveChunkData(lx, lz, buffer, length, algorithm);
        }

        // -------------------------------------------------------------------------
        // Private helpers
        // -------------------------------------------------------------------------

        private RegionFile GetRegion(Vector2Int regionCoord)
        {
            return _regions.GetOrAdd(regionCoord, coord => new Lazy<RegionFile>(() =>
            {
                string path = Path.Combine(_saveFolderPath, $"r.{coord.x}.{coord.y}.bin");
                return new RegionFile(path);
            })).Value;
        }

        private static ChunkData CreateSerializationSnapshot(ChunkData source)
        {
            ChunkData snapshot = World.Instance.ChunkPool.GetChunkData(source.Position);
            snapshot.NeedsInitialLighting = source.NeedsInitialLighting;

            // Copy Heightmap
            if (source.heightMap != null && snapshot.heightMap != null)
                Array.Copy(source.heightMap, snapshot.heightMap, source.heightMap.Length);

            // Copy compact sky levels
            Array.Copy(source.SectionUniformSkyLevel, snapshot.SectionUniformSkyLevel, source.SectionUniformSkyLevel.Length);

            // Correctly iterate the Section Array
            for (int i = 0; i < source.sections.Length; i++)
            {
                // Check specific section in source array
                if (source.sections[i] != null)
                {
                    ChunkSection snapSec = World.Instance.ChunkPool.GetChunkSection();
                    snapSec.nonAirCount = source.sections[i].nonAirCount;

                    // Copy voxels
                    Array.Copy(source.sections[i].voxels, snapSec.voxels, ChunkMath.SECTION_VOLUME);

                    // Skip LightData copy for compact sections — the sky byte carries the information.
                    if (source.SectionUniformSkyLevel[i] == ChunkData.UNIFORM_SKY_NONE)
                        Array.Copy(source.sections[i].LightData, snapSec.LightData, ChunkMath.SECTION_VOLUME);

                    // Assign to snapshot array
                    snapshot.sections[i] = snapSec;
                }
            }

            // Queue copying (Locking is correct)
            lock (source.SunlightBfsQueue)
            {
                foreach (LightQueueNode item in source.SunlightBfsQueue) snapshot.SunlightBfsQueue.Enqueue(item);
            }

            lock (source.BlocklightBfsQueue)
            {
                foreach (LightQueueNode item in source.BlocklightBfsQueue) snapshot.BlocklightBfsQueue.Enqueue(item);
            }

            return snapshot;
        }
    }
}
