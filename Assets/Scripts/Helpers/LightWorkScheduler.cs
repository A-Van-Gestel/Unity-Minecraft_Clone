using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;

namespace Helpers
{
    /// <summary>
    /// Bookkeeping for the light scheduler's dirty set, split into a <b>ready</b> set (visited by the
    /// per-frame scan in <c>World.Update</c>) and a <b>waiting</b> set of parked chunks whose readiness
    /// gates failed and will keep failing until a neighbor changes state (MT-2). Replaces the single
    /// <c>HashSet</c> whose full snapshot + gate re-test every frame went O(dirty) during exactly the
    /// backlog scenarios where frames are already slow.
    /// <para><b>Promotion contract:</b> a parked chunk re-enters the ready set only through
    /// (a) one of its own lighting flags transitioning to <c>true</c> (<see cref="Flag"/> via
    /// <c>ChunkData.OnLightWorkFlagged</c>), (b) an unblock event in its 3×3 neighborhood — terrain
    /// generation / disk load completed, or a lighting job completed (<see cref="PromoteNeighborhood"/>) —
    /// or (c) the ~1 s fail-safe scan (<see cref="PromoteAll"/>), which bounds any missed promotion at
    /// one scan period instead of a permanent stall.</para>
    /// <para><b>Threading:</b> <see cref="Flag"/> is the only thread-safe member (background
    /// deserialization threads flag chunks mid-load); everything else is main-thread only, mirroring
    /// the pipeline's flag-mutation contract.</para>
    /// </summary>
    public sealed class LightWorkScheduler
    {
        /// <summary>Chunks visited by the per-frame scan: flagged work whose gates have not (yet) failed.</summary>
        private readonly HashSet<Vector2Int> _ready = new HashSet<Vector2Int>();

        /// <summary>Parked chunks: flags still set, but a readiness gate failed — skipped by the scan until promoted.</summary>
        private readonly HashSet<Vector2Int> _waiting = new HashSet<Vector2Int>();

        /// <summary>Thread-safe staging for flag callbacks; drained into <see cref="_ready"/> once per frame.</summary>
        private readonly ConcurrentQueue<Vector2Int> _staging = new ConcurrentQueue<Vector2Int>();

        /// <summary>Number of chunks the per-frame scan will visit.</summary>
        public int ReadyCount => _ready.Count;

        /// <summary>Number of parked chunks awaiting a promotion event.</summary>
        public int WaitingCount => _waiting.Count;

        /// <summary>
        /// Registers a chunk whose lighting flag just transitioned to <c>true</c>. Safe to call from any
        /// thread; the position is staged and enters the ready set on the next <see cref="DrainStaging"/>.
        /// </summary>
        /// <param name="pos">Voxel-origin position of the flagged chunk.</param>
        public void Flag(Vector2Int pos)
        {
            _staging.Enqueue(pos);
        }

        /// <summary>
        /// Drains the thread-safe staging queue into the ready set, promoting any parked entries.
        /// Main-thread only; call once at the start of the lighting scan.
        /// </summary>
        public void DrainStaging()
        {
            while (_staging.TryDequeue(out Vector2Int pos))
            {
                AddReady(pos);
            }
        }

        /// <summary>
        /// Adds a chunk directly to the ready set (promoting it out of the waiting set if parked).
        /// Main-thread entry for the fail-safe scan; flag callbacks must use <see cref="Flag"/> instead.
        /// </summary>
        /// <param name="pos">Voxel-origin position of the chunk.</param>
        public void AddReady(Vector2Int pos)
        {
            _waiting.Remove(pos);
            _ready.Add(pos);
        }

        /// <summary>
        /// Appends the current ready set to <paramref name="buffer"/> so the scan can mutate the sets
        /// safely while iterating. The waiting set is deliberately excluded — that is the MT-2 win.
        /// </summary>
        /// <param name="buffer">Destination list (typically pooled); existing contents are preserved.</param>
        public void SnapshotReady(List<Vector2Int> buffer)
        {
            foreach (Vector2Int pos in _ready)
            {
                buffer.Add(pos);
            }
        }

        /// <summary>
        /// Parks a chunk whose readiness gate failed: moves it from ready to waiting so the per-frame
        /// scan skips it until a promotion event (or the fail-safe) moves it back.
        /// </summary>
        /// <param name="pos">Voxel-origin position of the blocked chunk.</param>
        public void MarkWaiting(Vector2Int pos)
        {
            _ready.Remove(pos);
            _waiting.Add(pos);
        }

        /// <summary>
        /// Forgets a chunk entirely (work complete or chunk unloaded), whichever set holds it.
        /// </summary>
        /// <param name="pos">Voxel-origin position of the chunk.</param>
        public void Remove(Vector2Int pos)
        {
            _ready.Remove(pos);
            _waiting.Remove(pos);
        }

        /// <summary>
        /// Promotes the parked entries in a chunk's 3×3 neighborhood (itself + 8 horizontal neighbors)
        /// back into the ready set. Call when an event occurs that can flip a lighting readiness gate
        /// for the neighborhood: the chunk finished terrain generation / disk load, or its lighting job
        /// completed. Move-only — positions not currently parked are not added.
        /// </summary>
        /// <param name="centerPos">Voxel-origin position of the chunk whose state changed.</param>
        /// <returns>The number of chunks promoted.</returns>
        public int PromoteNeighborhood(Vector2Int centerPos)
        {
            int promoted = 0;
            if (PromoteIfWaiting(centerPos)) promoted++;

            foreach (Vector3Int offset in VoxelData.AllNeighborOffsets)
            {
                Vector2Int neighborPos = new Vector2Int(
                    centerPos.x + offset.x * VoxelData.ChunkWidth,
                    centerPos.y + offset.z * VoxelData.ChunkWidth);
                if (PromoteIfWaiting(neighborPos)) promoted++;
            }

            return promoted;
        }

        /// <summary>
        /// Promotes every parked chunk back into the ready set. Fail-safe backstop: guarantees any
        /// promotion event this scheduler missed degrades to one fail-safe period of latency, never a
        /// permanent stall (the pipeline's deadlock history demands this).
        /// </summary>
        /// <returns>The number of chunks promoted — recurring non-zero counts indicate a missing
        /// promotion hook and deserve investigation.</returns>
        public int PromoteAll()
        {
            int promoted = _waiting.Count;
            if (promoted > 0)
            {
                _ready.UnionWith(_waiting);
                _waiting.Clear();
            }

            return promoted;
        }

        /// <summary>
        /// Empties both sets and flushes the staging queue. Call on world teardown/reload.
        /// </summary>
        public void Clear()
        {
            _ready.Clear();
            _waiting.Clear();
            // .NET Framework compatibility: ConcurrentQueue has no Clear() — drain it.
            while (_staging.TryDequeue(out _)) { }
        }

        /// <summary>Whether the per-frame scan will visit this chunk. Diagnostic/test accessor.</summary>
        /// <param name="pos">Voxel-origin position of the chunk.</param>
        /// <returns>True if the chunk is in the ready set.</returns>
        public bool IsReady(Vector2Int pos) => _ready.Contains(pos);

        /// <summary>Whether this chunk is parked awaiting a promotion event. Diagnostic/test accessor.</summary>
        /// <param name="pos">Voxel-origin position of the chunk.</param>
        /// <returns>True if the chunk is in the waiting set.</returns>
        public bool IsWaiting(Vector2Int pos) => _waiting.Contains(pos);

        /// <summary>Moves a single position from waiting to ready if it is parked.</summary>
        private bool PromoteIfWaiting(Vector2Int pos)
        {
            if (!_waiting.Remove(pos)) return false;
            _ready.Add(pos);
            return true;
        }
    }
}
