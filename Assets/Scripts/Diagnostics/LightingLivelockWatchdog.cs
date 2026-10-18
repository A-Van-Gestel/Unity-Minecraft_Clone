using System.Text;
using Data;
using Helpers;
using Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Diagnostics
{
    /// <summary>
    /// Tripwire for a lighting livelock (Lighting Bug 23): the same few chunks re-lit every frame, every merge unstable,
    /// while generation and meshing sit idle. Once tripped it records the next <see cref="CaptureMerges"/> merges —
    /// stability inputs, outbound mods and their routes, and the chunk's and its neighbors' lighting state — and writes
    /// them as one warning, once per world session.
    /// <para>
    /// Deliberately <b>not</b> gated on <c>UNITY_INCLUDE_INSTRUMENTATION</c>: the bug has only been seen in Master
    /// players, whose Release managed code variant compiles that symbol out. While silent it costs a few compares per
    /// lighting pass; it allocates only after tripping. Owned by <c>WorldJobManager</c> (one per world session), main
    /// thread only.
    /// </para>
    /// </summary>
    public sealed class LightingLivelockWatchdog
    {
        /// <summary>
        /// Consecutive merging lighting passes in which every merge was unstable, with no generation or mesh job in
        /// flight, that trip the capture. A healthy settle ends such a run within a few hundred passes.
        /// </summary>
        public const int TriggerPasses = 600;

        /// <summary>Merges recorded after tripping: several passes of each chunk in the loop.</summary>
        public const int CaptureMerges = 12;

        // Outbound mods listed per merge; the rest are only counted.
        private const int MAX_MODS_LISTED = 16;
        private const int REPORT_CAPACITY = 8192;

        private int _streak;
        private int _captureLeft;
        private int _modsListed;
        private StringBuilder _report;

        /// <summary>True once the watchdog has tripped this session (it never re-arms).</summary>
        public bool Tripped { get; private set; }

        /// <summary>True while merges are being recorded; callers gate their recording work on it.</summary>
        public bool Capturing => _captureLeft > 0;

        /// <summary>
        /// Feeds one lighting completion pass. Trips the capture after <see cref="TriggerPasses"/> consecutive merging
        /// passes that were all unstable while generation and meshing were idle.
        /// </summary>
        /// <param name="merged">Lighting jobs merged this pass.</param>
        /// <param name="unstable">Of those, the ones that ended unstable.</param>
        /// <param name="othersIdle">True when no generation or mesh job is in flight.</param>
        public void ObservePass(int merged, int unstable, bool othersIdle)
        {
            if (!othersIdle || unstable < merged)
            {
                _streak = 0;
                return;
            }

            if (merged == 0 || Tripped || ++_streak < TriggerPasses) return;

            Tripped = true;
            _captureLeft = CaptureMerges;
            _report = new StringBuilder(REPORT_CAPACITY);
            _report.Append("[LIGHTING] Bug 23 watchdog: ").Append(TriggerPasses)
                .Append(" consecutive lighting passes merged only unstable jobs while generation and meshing were idle (frame ")
                .Append(Time.frameCount).Append("). Next ").Append(CaptureMerges).Append(" merges:");
        }

        /// <summary>Resets the streak when no lighting job is in flight, writing out a capture that cannot finish.</summary>
        public void ObserveNoLightingJobs()
        {
            _streak = 0;
            if (Capturing) Flush("lighting went idle before the capture finished");
        }

        /// <summary>Records a lighting job being scheduled while capturing, with the scan arm that chose it.</summary>
        /// <param name="coord">The chunk scheduled.</param>
        /// <param name="action">The scan arm (<see cref="LightingScanDecision"/>).</param>
        /// <param name="work">The chunk's lighting work bits just before the schedule consumed them.</param>
        public void RecordSchedule(ChunkCoord coord, LightingScanDecision.ScanAction action, LightingWork work)
        {
            _report.Append("\n  schedule ").Append(coord.X).Append(',').Append(coord.Z)
                .Append(" arm=").Append(action.ToString()).Append(" work=").Append(work.ToString());
        }

        /// <summary>Opens one merge's record.</summary>
        /// <param name="coord">The merged chunk.</param>
        /// <param name="bandMinY">The job's LI-2 band bottom.</param>
        /// <param name="bandHeight">The job's LI-2 band top (exclusive).</param>
        /// <param name="edgeCheck">Whether the job ran its edge check.</param>
        /// <param name="seeds">The job's seed sky nodes, block nodes and recalc columns; zero when it was scheduled
        /// before the capture began.</param>
        /// <param name="jobStable">The stability the job itself reported.</param>
        /// <param name="modCount">Outbound cross-chunk mods the job emitted.</param>
        /// <param name="claimCount">Pull-back claims the job recorded.</param>
        public void BeginMerge(ChunkCoord coord, int bandMinY, int bandHeight, bool edgeCheck, int3 seeds,
            bool jobStable, int modCount, int claimCount)
        {
            _modsListed = 0;
            _report.Append("\n  merge ").Append(coord.X).Append(',').Append(coord.Z)
                .Append(" frame=").Append(Time.frameCount)
                .Append(" band=[").Append(bandMinY).Append(',').Append(bandHeight).Append(')')
                .Append(" edge=").Append(edgeCheck)
                .Append(" seeds=").Append(seeds.x).Append('/').Append(seeds.y).Append('/').Append(seeds.z)
                .Append(" jobStable=").Append(jobStable)
                .Append(" mods=").Append(modCount).Append(" claims=").Append(claimCount);
        }

        /// <summary>Records the pull-back claims the merge cleared as stale.</summary>
        /// <param name="cleared">Stale claims cleared.</param>
        public void RecordStalePullBacks(int cleared)
        {
            _report.Append(" staleClaims=").Append(cleared);
        }

        /// <summary>Records the deferred mods drained into the merged chunk.</summary>
        /// <param name="drained">Deferred mods drained.</param>
        /// <param name="effective">Of those, the ones that changed the chunk's light.</param>
        public void RecordDrain(int drained, int effective)
        {
            _report.Append(" drained=").Append(drained).Append('/').Append(effective).Append(" effective");
        }

        /// <summary>Records one outbound mod and what its route did with it.</summary>
        /// <param name="mod">The mod.</param>
        /// <param name="route">Its route.</param>
        /// <param name="targetLightBefore">The target voxel's packed light before an apply; 0 for other routes.</param>
        /// <param name="applied">Whether a direct apply changed the target.</param>
        public void RecordMod(in LightModification mod, LightingJobProcessor.CrossChunkModRoute route,
            ushort targetLightBefore, bool applied)
        {
            if (_modsListed++ >= MAX_MODS_LISTED) return;

            _report.Append("\n    mod (").Append(mod.GlobalPosition.x).Append(',').Append(mod.GlobalPosition.y)
                .Append(',').Append(mod.GlobalPosition.z).Append(") ").Append(mod.Channel.ToString())
                .Append(" level=").Append(mod.LightLevel)
                .Append(" rgb=").Append(mod.BlockR).Append('/').Append(mod.BlockG).Append('/').Append(mod.BlockB)
                .Append(" removal=").Append(mod.IsRemoval).Append(" route=").Append(route.ToString())
                .Append(" before=0x").Append(targetLightBefore.ToString("X4")).Append(" applied=").Append(applied);
        }

        /// <summary>Closes one merge's record with its stability verdict and the chunk's resulting state.</summary>
        /// <param name="lightChanged">Whether the merge changed the chunk's light.</param>
        /// <param name="hasRealCrossChunkMods">Whether any mod counted toward instability.</param>
        /// <param name="effectiveStable">The stability after the out-of-world override.</param>
        /// <param name="chunk">The merged chunk's data, or null.</param>
        public void EndMerge(bool lightChanged, bool hasRealCrossChunkMods, bool effectiveStable, ChunkData chunk)
        {
            if (_modsListed > MAX_MODS_LISTED)
                _report.Append("\n    (+").Append(_modsListed - MAX_MODS_LISTED).Append(" more mods)");

            _report.Append("\n    -> lightChanged=").Append(lightChanged).Append(" realMods=").Append(hasRealCrossChunkMods)
                .Append(" stable=").Append(effectiveStable);
            AppendChunkState(chunk);
        }

        /// <summary>Records one neighbor's lighting state under the current merge.</summary>
        /// <param name="dx">Neighbor X offset.</param>
        /// <param name="dz">Neighbor Z offset.</param>
        /// <param name="neighbor">The neighbor's data, or null when absent.</param>
        /// <param name="inFlight">Whether the neighbor has a lighting job in flight.</param>
        public void RecordNeighbor(int dx, int dz, ChunkData neighbor, bool inFlight)
        {
            _report.Append("\n      n(").Append(dx).Append(',').Append(dz).Append(") inFlight=").Append(inFlight);
            AppendChunkState(neighbor);
        }

        /// <summary>Counts one finished merge record, writing the report out after the last.</summary>
        public void CompleteMerge()
        {
            if (--_captureLeft == 0) Flush("capture complete");
        }

        private void AppendChunkState(ChunkData chunk)
        {
            if (chunk == null)
            {
                _report.Append(" absent");
                return;
            }

            _report.Append(" populated=").Append(chunk.IsPopulated).Append(" work=").Append(chunk.Work.ToString())
                .Append(" edgeRounds=").Append(chunk.RemainingEdgeCheckRounds);
        }

        private void Flush(string reason)
        {
            _captureLeft = 0;
            _report.Append("\n  (").Append(reason).Append(')');
            Debug.LogWarning(_report.ToString());
            _report = null;
        }
    }
}
