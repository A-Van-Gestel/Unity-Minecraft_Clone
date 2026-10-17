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

    // The first pass plus one re-planned pass over the targets it left behind.
    private const int PASSES = 2;

    private const int REPORTED_STALLED_CHUNKS = 8;

    private readonly World _world;
    private bool _cancelRequested;
    private string _cancelReason;

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
    /// True for the console command: the player is returned to the start through <c>TeleportPlayer</c> and the
    /// modified chunks are saved asynchronously at the end. False for the benchmark, which teleports and saves itself.
    /// </param>
    /// <returns>The coroutine enumerator.</returns>
    public IEnumerator Run(ChunkRect rect, bool interactive)
    {
        IsRunning = true;
        _cancelRequested = false;
        _cancelReason = null;

        Stopwatch elapsed = Stopwatch.StartNew();
        PregenerationOutcome outcome = new PregenerationOutcome { Rect = rect };
        int loadDistance = _world.settings.LoadDistance;
        int coverRadius = PregenerationPlan.CoverRadius(loadDistance);
        bool hasStart = _world.TryGetPlayerVoxelCell(out int startX, out int startY, out int startZ);
        bool holdHandedOff = false;

        HashSet<ChunkCoord> missing = HashSetPool<ChunkCoord>.Get();
        List<ChunkCoord> grid = ListPool<ChunkCoord>.Get();
        List<ChunkCoord> stations = ListPool<ChunkCoord>.Get();
        List<ChunkCoord> targets = ListPool<ChunkCoord>.Get();

        // Held in both modes: an unheld player could drift off the station, which the walk reads as an outside move.
        _world.SetPregenerationHold(true);
        try
        {
            if (!TryCollectMissing(rect, missing, out string scanError))
            {
                _cancelRequested = true;
                _cancelReason = scanError;
            }

            PregenerationPlan.BuildStationGrid(rect, coverRadius, grid);
            PregenerationPlan.FilterStations(grid, coverRadius, missing, stations, targets);
            outcome.MissingAtStart = missing.Count;
            outcome.Stations = stations.Count;
            Debug.Log($"[Pregen] {rect}: {missing.Count} missing, {stations.Count} stations " +
                      $"(cover radius {coverRadius}, LoadDistance {loadDistance}).");

            for (int pass = 0; pass < PASSES && missing.Count > 0 && !_cancelRequested; pass++)
            {
                if (pass > 0) PregenerationPlan.FilterStations(grid, coverRadius, missing, stations, targets);

                for (int i = 0; i < stations.Count && !_cancelRequested; i++)
                {
                    yield return WalkStation(stations[i], coverRadius, loadDistance, missing, targets, outcome);
                    _world.RaisePregenerationProgress(outcome.Generated, outcome.MissingAtStart, i + 1, stations.Count);
                }
            }

            outcome.ElapsedSeconds = elapsed.Elapsed.TotalSeconds;
            if (_cancelRequested)
            {
                outcome.Status = PregenerationStatus.Canceled;
                outcome.CancelReason = _cancelReason;
            }
            else if (missing.Count > 0)
            {
                outcome.Status = PregenerationStatus.CompletedWithStalls;
                outcome.Stalled = missing.Count;
                foreach (ChunkCoord chunkCoord in missing)
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
                // TeleportPlayer re-arms the arrival hold, whose release clears the flag set above.
                if (hasStart)
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
            HashSetPool<ChunkCoord>.Release(missing);
            ListPool<ChunkCoord>.Release(grid);
            ListPool<ChunkCoord>.Release(stations);
            ListPool<ChunkCoord>.Release(targets);
            IsRunning = false;
        }

        LastOutcome = outcome;
        _world.RaisePregenerationEnded(outcome);
    }

    /// <summary>
    /// Fills the missing set (§4.2): the rectangle minus chunks resident, populated and with no tracked generation
    /// job, minus chunks with a record on disk.
    /// </summary>
    /// <param name="rect">The rectangle.</param>
    /// <param name="missing">Destination; assumed empty.</param>
    /// <param name="error">Why the scan failed, or null.</param>
    /// <returns>False when the region files could not be read.</returns>
    private bool TryCollectMissing(ChunkRect rect, HashSet<ChunkCoord> missing, out string error)
    {
        HashSet<ChunkCoord> saved = HashSetPool<ChunkCoord>.Get();
        try
        {
            _world.StorageManager.CollectSavedChunks(rect, saved);
        }
        catch (Exception e)
        {
            HashSetPool<ChunkCoord>.Release(saved);
            error = $"the region files could not be read ({e.Message})";
            Debug.LogError($"[Pregen] Scan of {rect} failed: {e}");
            return false;
        }

        WorldJobManager jobs = _world.JobManager;
        for (int z = rect.MinZ; z <= rect.MaxZ; z++)
        {
            for (int x = rect.MinX; x <= rect.MaxX; x++)
            {
                ChunkCoord chunkCoord = new ChunkCoord(x, z);
                if (saved.Contains(chunkCoord)) continue;

                bool readyInMemory = _world.worldData.TryGetChunk(chunkCoord.ToVoxelOrigin(), out ChunkData data)
                                     && data.IsPopulated && !jobs.GenerationJobs.ContainsKey(chunkCoord);
                if (!readyInMemory) missing.Add(chunkCoord);
            }
        }

        HashSetPool<ChunkCoord>.Release(saved);
        error = null;
        return true;
    }

    /// <summary>
    /// Moves the anchor to one station and waits until each target in its cover square is settled, or until a
    /// watchdog gives up on it. Finished targets leave <paramref name="missing"/>; stalled ones stay for the retry.
    /// </summary>
    /// <param name="station">The station chunk.</param>
    /// <param name="coverRadius">The cover radius.</param>
    /// <param name="loadDistance">The <c>LoadDistance</c> the run started with.</param>
    /// <param name="missing">The chunks still to generate.</param>
    /// <param name="targets">Reusable target list.</param>
    /// <param name="outcome">Counters to update.</param>
    /// <returns>The coroutine enumerator.</returns>
    private IEnumerator WalkStation(ChunkCoord station, int coverRadius, int loadDistance,
        HashSet<ChunkCoord> missing, List<ChunkCoord> targets, PregenerationOutcome outcome)
    {
        _world.MovePregenerationAnchor(station);
        PregenerationPlan.CollectTargets(station, coverRadius, missing, targets);

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
                if (ShouldCancel(station, loadDistance)) yield break;

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
                        FinishTarget(targets, t, missing, outcome);
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
                        // Stalled: the pending targets stay missing for the retry pass; populated ones are done.
                        FinishUnsettled(targets, missing, outcome);
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
                    FinishUnsettled(targets, missing, outcome);
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

    /// <summary>Removes one finished target from the station and the missing set.</summary>
    private static void FinishTarget(List<ChunkCoord> targets, int index, HashSet<ChunkCoord> missing,
        PregenerationOutcome outcome)
    {
        missing.Remove(targets[index]);
        targets[index] = targets[targets.Count - 1];
        targets.RemoveAt(targets.Count - 1);
        outcome.Generated++;
    }

    /// <summary>Finishes every populated-but-unsettled target as a settle timeout; pending ones stay missing.</summary>
    private void FinishUnsettled(List<ChunkCoord> targets, HashSet<ChunkCoord> missing, PregenerationOutcome outcome)
    {
        WorldJobManager jobs = _world.JobManager;
        for (int t = targets.Count - 1; t >= 0; t--)
        {
            ChunkCoord chunkCoord = targets[t];
            bool populated = _world.worldData.TryGetChunk(chunkCoord.ToVoxelOrigin(), out ChunkData data)
                             && data.IsPopulated && !jobs.GenerationJobs.ContainsKey(chunkCoord);
            if (!populated) continue;

            outcome.SettleTimeouts++;
            FinishTarget(targets, t, missing, outcome);
        }

        targets.Clear();
    }

    /// <summary>Whether the run must stop: a cancel request, a view-distance change, or an anchor moved from outside.</summary>
    /// <param name="station">The station the anchor was placed on.</param>
    /// <param name="loadDistance">The <c>LoadDistance</c> the run started with.</param>
    /// <returns>True to stop.</returns>
    private bool ShouldCancel(ChunkCoord station, int loadDistance)
    {
        if (_cancelRequested) return true;

        if (_world.settings.LoadDistance != loadDistance)
        {
            _cancelRequested = true;
            _cancelReason = "the view distance changed";
        }
        else if (!_world.PlayerChunkCoord.Equals(station))
        {
            _cancelRequested = true;
            _cancelReason = "the player was moved away from the station";
        }

        return _cancelRequested;
    }

    /// <summary>Logs the outcome: one line, plus an error for stalls and a warning for settle timeouts.</summary>
    /// <param name="outcome">The finished outcome.</param>
    private static void Report(PregenerationOutcome outcome)
    {
        Debug.Log($"[Pregen] {outcome.Status} over {outcome.Rect}: {outcome.Generated} of {outcome.MissingAtStart} " +
                  $"missing generated, {outcome.Stations} stations, {outcome.ElapsedSeconds:F1} s " +
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
