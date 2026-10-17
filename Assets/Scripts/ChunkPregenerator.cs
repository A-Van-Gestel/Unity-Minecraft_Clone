using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using Data;
using Helpers;
using UnityEngine.Pool;
using Debug = UnityEngine.Debug;
using Readiness = Helpers.PregenerationPlan.Readiness;

/// <summary>
/// The CMD-6 station walk: makes every chunk of a rectangle exist on disk (or resident and marked for saving) by
/// moving the pipeline's single residency anchor through a grid of stations and, at each, waiting until the
/// missing chunks in reach are populated and their lighting has settled. Owned by <see cref="World"/>; one
/// coroutine serves both the console command and the benchmark.
/// <para>
/// The walk streams: it checks one station's cover square at a time (residency plus the region headers) and moves
/// the anchor only to stations holding a missing chunk. A station's missing set is captured before the first of its
/// grid neighbors is walked, because that walk streams chunks into its cover that must still be waited on there.
/// Memory is therefore the next row's captured sets plus the stalled set — it grows with the rectangle's width, not
/// its area. A canceled or interrupted run resumes for free: re-running it reads what is already on disk.
/// </para>
/// <para>
/// The pipeline is not changed: no flag, gate, admission or unload rule is touched. The walk reads chunk state
/// and the job dictionaries on the main thread and moves the anchor; its one write is re-marking a settled
/// chunk for saving when it was reloaded from an unsettled save, so its finished lighting reaches disk.
/// </para>
/// See Documentation/Design/CHUNK_PREGENERATION.md §4.
/// </summary>
public sealed class ChunkPregenerator
{
    // No newly populated target for this long means the station will not converge. Long by design: a station
    // legitimately waits out every panic-gate closure, and its duration scales with view distance.
    private const double STALL_SECONDS = 30.0;

    // Settling is best effort (§3.2): once every target is populated, a short quiet spell ends the wait.
    private const double SETTLE_TIMEOUT_SECONDS = 3.0;

    // Main-thread time the scan of already-generated stations may take per frame, so a huge rectangle that is
    // mostly on disk is skimmed over several frames instead of freezing one.
    private const double SCAN_BUDGET_MS = 4.0;

    private const int REPORTED_STALLED_CHUNKS = 8;

    private readonly World _world;
    private bool _cancelRequested;
    private string _cancelReason;
    private bool _anchorMoved;
    private ChunkCoord _anchorChunk;

    /// <summary>Creates the pre-generator for a world.</summary>
    /// <param name="world">The owning world.</param>
    public ChunkPregenerator(World world)
    {
        _world = world;
    }

    /// <summary>Whether a run is active.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>The outcome of the most recent finished run, or null.</summary>
    public PregenerationOutcome LastOutcome { get; private set; }

    /// <summary>Asks the active run to stop at its next frame.</summary>
    /// <param name="reason">Why, for the outcome.</param>
    /// <returns>False when no run is active.</returns>
    public bool RequestCancel(string reason)
    {
        if (!IsRunning) return false;
        _cancelRequested = true;
        _cancelReason = reason;
        return true;
    }

    /// <summary>
    /// The station walk over <paramref name="rect"/>. The caller has already validated the start
    /// (<see cref="PregenerationPlan.ValidateStart"/>).
    /// </summary>
    /// <param name="rect">The chunks to make exist.</param>
    /// <param name="interactive">
    /// True for the console command: the modified chunks are saved asynchronously at the end, and a player the walk
    /// moved is returned to the start through <c>TeleportPlayer</c>. False for the benchmark, which teleports and
    /// saves itself.
    /// </param>
    /// <returns>The coroutine enumerator.</returns>
    public IEnumerator Run(ChunkRect rect, bool interactive)
    {
        IsRunning = true;
        _cancelRequested = false;
        _cancelReason = null;
        _anchorMoved = false;

        Stopwatch elapsed = Stopwatch.StartNew();
        int loadDistance = _world.settings.LoadDistance;
        int coverRadius = PregenerationPlan.CoverRadius(loadDistance);
        PregenerationOutcome outcome = new PregenerationOutcome
        {
            Rect = rect,
            StationGridCount = PregenerationPlan.StationGridCount(rect, coverRadius),
        };
        bool hasStart = _world.TryGetPlayerVoxelCell(out int startX, out int startY, out int startZ);
        bool holdHandedOff = false;

        HashSet<ChunkCoord> stalled = HashSetPool<ChunkCoord>.Get();
        HashSet<ChunkCoord> saved = HashSetPool<ChunkCoord>.Get();
        List<ChunkCoord> targets = ListPool<ChunkCoord>.Get();
        List<long> retryStations = ListPool<long>.Get();
        Dictionary<long, List<ChunkCoord>> captured = DictionaryPool<long, List<ChunkCoord>>.Get();

        // Held in both modes: an unheld player could drift off the station, which the walk reads as an outside move.
        _world.SetPregenerationHold(true);
        try
        {
            // StartCoroutine runs a body up to its first yield at once, so without this a run with nothing to walk
            // would finish inside the console command that started it and report before the command's own reply.
            yield return null;

            _anchorChunk = _world.PlayerChunkCoord;
            Debug.Log($"[Pregen] {rect}: {rect.Count} chunks, {outcome.StationGridCount} stations " +
                      $"(cover radius {coverRadius}, LoadDistance {loadDistance}).");

            Stopwatch frame = Stopwatch.StartNew();
            for (long i = 0; i < outcome.StationGridCount; i++)
            {
                if (frame.Elapsed.TotalMilliseconds > SCAN_BUDGET_MS)
                {
                    yield return null;
                    frame.Restart();
                }

                if (ShouldCancel(loadDistance)) break;

                ChunkCoord station = PregenerationPlan.StationAt(rect, coverRadius, i);
                if (captured.TryGetValue(i, out List<ChunkCoord> early))
                {
                    captured.Remove(i);
                    targets.Clear();
                    targets.AddRange(early);
                    ListPool<ChunkCoord>.Release(early);
                }
                else if (!TryCollectStationMissing(rect, station, coverRadius, saved, targets))
                {
                    break;
                }

                outcome.StationsChecked = i + 1;
                if (targets.Count > 0)
                {
                    if (!CaptureUnvisitedNeighbors(rect, coverRadius, i, captured, saved)) break;

                    outcome.MissingFound += targets.Count;
                    outcome.Stations++;
                    yield return WalkStation(station, loadDistance, targets, stalled, outcome);
                    frame.Restart();
                }

                // A station cut short by a cancel reports through the outcome alone, not as one more step of progress.
                if (!_cancelRequested)
                    _world.RaisePregenerationProgress(new PregenerationProgressReport(outcome.StationsChecked,
                        outcome.StationGridCount, outcome.Generated, elapsed.Elapsed.TotalSeconds));
            }

            // One retry pass over the stations still holding a stalled target, in walk order.
            if (stalled.Count > 0 && !_cancelRequested)
            {
                foreach (ChunkCoord chunkCoord in stalled)
                    retryStations.Add(PregenerationPlan.StationIndexOf(rect, coverRadius, chunkCoord));
                retryStations.Sort();

                long previous = -1;
                foreach (long index in retryStations)
                {
                    if (index == previous) continue;
                    previous = index;
                    if (ShouldCancel(loadDistance)) break;

                    ChunkCoord station = PregenerationPlan.StationAt(rect, coverRadius, index);
                    PregenerationPlan.CollectTargets(station, coverRadius, stalled, targets);
                    if (targets.Count > 0) yield return WalkStation(station, loadDistance, targets, stalled, outcome);
                }
            }

            outcome.ElapsedSeconds = elapsed.Elapsed.TotalSeconds;
            if (_cancelRequested)
            {
                outcome.Status = PregenerationStatus.Canceled;
                outcome.CancelReason = _cancelReason;
            }
            else if (stalled.Count > 0)
            {
                outcome.Status = PregenerationStatus.CompletedWithStalls;
                outcome.Stalled = stalled.Count;
                foreach (ChunkCoord chunkCoord in stalled)
                {
                    if (outcome.FirstStalled.Count >= REPORTED_STALLED_CHUNKS) break;
                    outcome.FirstStalled.Add(chunkCoord);
                }
            }
            else
            {
                outcome.Status = PregenerationStatus.Completed;
            }

            if (interactive)
            {
                // TeleportPlayer re-arms the arrival hold, whose release clears the flag set above. A run that never
                // moved the anchor leaves the player where it stood; the finally block releases the hold instead.
                if (hasStart && _anchorMoved)
                {
                    _world.TeleportPlayer(startX, startY, startZ, resolveSurfaceY: false);
                    holdHandedOff = true;
                }

                _world.SaveWorldData();
            }

            Report(outcome);
        }
        finally
        {
            if (!holdHandedOff) _world.SetPregenerationHold(false);
            HashSetPool<ChunkCoord>.Release(stalled);
            HashSetPool<ChunkCoord>.Release(saved);
            ListPool<ChunkCoord>.Release(targets);
            ListPool<long>.Release(retryStations);
            foreach (List<ChunkCoord> list in captured.Values) ListPool<ChunkCoord>.Release(list);
            DictionaryPool<long, List<ChunkCoord>>.Release(captured);
            IsRunning = false;
        }

        LastOutcome = outcome;
        _world.RaisePregenerationEnded(outcome);
    }

    /// <summary>
    /// Fills one station's targets (§4.2): its cover square clipped to the rectangle, minus chunks resident,
    /// populated and with no tracked generation job, minus chunks with a record on disk.
    /// </summary>
    /// <param name="rect">The rectangle.</param>
    /// <param name="station">The station chunk.</param>
    /// <param name="coverRadius">The cover radius.</param>
    /// <param name="saved">Reusable scratch set for the region-header read.</param>
    /// <param name="into">Destination; cleared first.</param>
    /// <returns>False when the region files could not be read; the run is then canceled with the reason.</returns>
    private bool TryCollectStationMissing(ChunkRect rect, ChunkCoord station, int coverRadius,
        HashSet<ChunkCoord> saved, List<ChunkCoord> into)
    {
        into.Clear();
        saved.Clear();
        ChunkRect cover = PregenerationPlan.ClipCover(station, coverRadius, rect);
        try
        {
            _world.StorageManager.CollectSavedChunks(cover, saved);
        }
        catch (Exception e)
        {
            _cancelRequested = true;
            _cancelReason = $"the region files could not be read ({e.Message})";
            Debug.LogError($"[Pregen] Scan of {cover} failed: {e}");
            return false;
        }

        WorldJobManager jobs = _world.JobManager;
        for (int z = cover.MinZ; z <= cover.MaxZ; z++)
        {
            for (int x = cover.MinX; x <= cover.MaxX; x++)
            {
                ChunkCoord chunkCoord = new ChunkCoord(x, z);
                if (saved.Contains(chunkCoord)) continue;

                bool readyInMemory = _world.worldData.TryGetChunk(chunkCoord.ToVoxelOrigin(), out ChunkData data)
                                     && data.IsPopulated && !jobs.GenerationJobs.ContainsKey(chunkCoord);
                if (!readyInMemory) into.Add(chunkCoord);
            }
        }

        return true;
    }

    /// <summary>
    /// Captures the missing set of every not-yet-visited grid neighbor of a station about to be walked. None of their
    /// other neighbors has been walked yet (that walk would have captured them first), so the chunks this walk streams
    /// into their covers stay targets there and are waited on until their lighting settles.
    /// </summary>
    /// <param name="rect">The rectangle.</param>
    /// <param name="coverRadius">The cover radius.</param>
    /// <param name="index">The walk index of the station about to be walked.</param>
    /// <param name="captured">Captured sets by walk index.</param>
    /// <param name="saved">Reusable scratch set for the region-header read.</param>
    /// <returns>False when the region files could not be read; the run is then canceled with the reason.</returns>
    private bool CaptureUnvisitedNeighbors(ChunkRect rect, int coverRadius, long index,
        Dictionary<long, List<ChunkCoord>> captured, HashSet<ChunkCoord> saved)
    {
        for (int rowOffset = -1; rowOffset <= 1; rowOffset++)
        {
            for (int columnOffset = -1; columnOffset <= 1; columnOffset++)
            {
                if (!PregenerationPlan.TryGetNeighborStation(rect, coverRadius, index, columnOffset, rowOffset,
                        out long neighbor) || neighbor <= index || captured.ContainsKey(neighbor))
                    continue;

                List<ChunkCoord> list = ListPool<ChunkCoord>.Get();
                if (!TryCollectStationMissing(rect, PregenerationPlan.StationAt(rect, coverRadius, neighbor), coverRadius,
                        saved, list))
                {
                    ListPool<ChunkCoord>.Release(list);
                    return false;
                }

                captured.Add(neighbor, list);
            }
        }

        return true;
    }

    /// <summary>
    /// Moves the anchor to one station and waits until each target is settled, or until a watchdog gives up on it.
    /// Finished targets leave <paramref name="stalled"/>; targets a stall leaves pending join it for the retry pass.
    /// Returns with <paramref name="targets"/> empty.
    /// </summary>
    /// <param name="station">The station chunk.</param>
    /// <param name="loadDistance">The <c>LoadDistance</c> the run started with.</param>
    /// <param name="targets">The station's missing chunks.</param>
    /// <param name="stalled">Chunks that never populated at their station so far.</param>
    /// <param name="outcome">Counters to update.</param>
    /// <returns>The coroutine enumerator.</returns>
    private IEnumerator WalkStation(ChunkCoord station, int loadDistance, List<ChunkCoord> targets,
        HashSet<ChunkCoord> stalled, PregenerationOutcome outcome)
    {
        _anchorMoved = true;
        _anchorChunk = station;
        _world.MovePregenerationAnchor(station);

        int targetCount = targets.Count;
        int lastReached = -1;
        int lastResolved = -1;
        bool settling = false;
        Stopwatch sinceProgress = Stopwatch.StartNew();
        Stopwatch stationClock = Stopwatch.StartNew();
        double populatedAt = -1.0;
        WorldJobManager jobs = _world.JobManager;

        try
        {
            while (targets.Count > 0)
            {
                yield return null;
                if (ShouldCancel(loadDistance))
                {
                    targets.Clear();
                    yield break;
                }

                bool lightingEnabled = _world.settings.enableLighting;
                int populated = 0;
                int pending = 0;

                for (int t = targets.Count - 1; t >= 0; t--)
                {
                    ChunkCoord chunkCoord = targets[t];
                    bool resident = _world.worldData.TryGetChunk(chunkCoord.ToVoxelOrigin(), out ChunkData data);
                    Readiness readiness = PregenerationPlan.Classify(new PregenerationPlan.ReadinessFacts(
                        resident,
                        resident && data.IsPopulated,
                        jobs.GenerationJobs.ContainsKey(chunkCoord),
                        resident && data.HasAnyLightingWork,
                        jobs.LightingJobs.ContainsKey(chunkCoord),
                        lightingEnabled));

                    if (readiness == Readiness.Settled)
                    {
                        // A chunk saved unsettled by an earlier station's unload and reloaded here is not in the save
                        // set (only Populate and ModifyVoxel add to it); re-marking it saves the lighting it now has.
                        if (_world.worldData.ModifiedChunks.Add(data)) outcome.Remarked++;
                        FinishTarget(targets, t, stalled, outcome);
                    }
                    else if (readiness == Readiness.Populated)
                    {
                        populated++;
                    }
                    else
                    {
                        pending++;
                    }
                }

                int resolved = targetCount - targets.Count;
                int reached = resolved + populated;

                if (pending > 0)
                {
                    if (reached > lastReached)
                    {
                        lastReached = reached;
                        sinceProgress.Restart();
                    }
                    else if (sinceProgress.Elapsed.TotalSeconds > STALL_SECONDS)
                    {
                        // Stalled: the pending targets go to the retry pass; populated ones are done.
                        FinishUnsettled(targets, stalled, outcome);
                        yield break;
                    }

                    continue;
                }

                if (populatedAt < 0.0) populatedAt = stationClock.Elapsed.TotalSeconds;

                if (!settling || resolved > lastResolved)
                {
                    settling = true;
                    lastResolved = resolved;
                    sinceProgress.Restart();
                }
                else if (sinceProgress.Elapsed.TotalSeconds > SETTLE_TIMEOUT_SECONDS)
                {
                    FinishUnsettled(targets, stalled, outcome);
                    yield break;
                }
            }
        }
        finally
        {
            // Every exit is timed, the stall and cancel ones included: those are exactly the stations a report reads.
            double stationSeconds = stationClock.Elapsed.TotalSeconds;
            double populateSeconds = populatedAt < 0.0 ? stationSeconds : populatedAt;
            outcome.PopulateSeconds += populateSeconds;
            outcome.SettleSeconds += stationSeconds - populateSeconds;
            if (stationSeconds > outcome.SlowestStationSeconds)
            {
                outcome.SlowestStationSeconds = stationSeconds;
                outcome.SlowestStation = station;
            }
        }
    }

    /// <summary>Removes one finished target from the station and from the stalled set.</summary>
    private static void FinishTarget(List<ChunkCoord> targets, int index, HashSet<ChunkCoord> stalled,
        PregenerationOutcome outcome)
    {
        stalled.Remove(targets[index]);
        targets[index] = targets[targets.Count - 1];
        targets.RemoveAt(targets.Count - 1);
        outcome.Generated++;
    }

    /// <summary>
    /// Finishes every populated-but-unsettled target as a settle timeout; the still-pending ones join the stalled set.
    /// </summary>
    private void FinishUnsettled(List<ChunkCoord> targets, HashSet<ChunkCoord> stalled, PregenerationOutcome outcome)
    {
        WorldJobManager jobs = _world.JobManager;
        for (int t = targets.Count - 1; t >= 0; t--)
        {
            ChunkCoord chunkCoord = targets[t];
            bool populated = _world.worldData.TryGetChunk(chunkCoord.ToVoxelOrigin(), out ChunkData data)
                             && data.IsPopulated && !jobs.GenerationJobs.ContainsKey(chunkCoord);
            if (!populated) continue;

            outcome.SettleTimeouts++;
            FinishTarget(targets, t, stalled, outcome);
        }

        foreach (ChunkCoord chunkCoord in targets) stalled.Add(chunkCoord);
        targets.Clear();
    }

    /// <summary>Whether the run must stop: a cancel request, a view-distance change, or an anchor moved from outside.</summary>
    /// <param name="loadDistance">The <c>LoadDistance</c> the run started with.</param>
    /// <returns>True to stop.</returns>
    private bool ShouldCancel(int loadDistance)
    {
        if (_cancelRequested) return true;

        if (_world.settings.LoadDistance != loadDistance)
        {
            _cancelRequested = true;
            _cancelReason = "the view distance changed";
        }
        else if (!_world.PlayerChunkCoord.Equals(_anchorChunk))
        {
            _cancelRequested = true;
            _cancelReason = "the player was moved during the run";
        }

        return _cancelRequested;
    }

    /// <summary>Logs the outcome: one line, plus an error for stalls and a warning for settle timeouts.</summary>
    /// <param name="outcome">The finished outcome.</param>
    private static void Report(PregenerationOutcome outcome)
    {
        Debug.Log($"[Pregen] {outcome.Status} over {outcome.Rect}: {outcome.Generated} of {outcome.MissingFound} " +
                  $"missing generated, {outcome.Stations} stations walked of {outcome.StationsChecked} checked " +
                  $"({outcome.StationGridCount} in the grid), {outcome.ElapsedSeconds:F1} s " +
                  $"(populate {outcome.PopulateSeconds:F1} s + settle {outcome.SettleSeconds:F1} s, slowest station " +
                  $"{outcome.SlowestStationSeconds:F1} s at {outcome.SlowestStation}), " +
                  $"{outcome.Stalled} stalled, {outcome.SettleTimeouts} settle timeouts, {outcome.Remarked} re-marked" +
                  (outcome.CancelReason != null ? $" — canceled: {outcome.CancelReason}." : "."));

        if (outcome.Stalled > 0)
            Debug.LogError($"[Pregen] {outcome.Stalled} chunks never populated after the retry pass, first: " +
                           string.Join(", ", outcome.FirstStalled) + ".");

        if (outcome.SettleTimeouts > 0)
            Debug.LogWarning($"[Pregen] {outcome.SettleTimeouts} generated chunks did not settle their lighting " +
                             "before the settle watchdog moved on.");
    }
}
