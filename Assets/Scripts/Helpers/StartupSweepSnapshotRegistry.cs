using System;
using System.Collections.Generic;
using Data;
using Unity.Collections;

namespace Helpers
{
    /// <summary>
    /// The startup sweep snapshot sets alive at once, by id: at most one is open to new jobs, and each lives until its
    /// last job is released. A job stores only the id (a blittable int), so its job data stays Burst-safe; id 0 means
    /// "no shared set". Main thread only.
    /// </summary>
    public sealed class StartupSweepSnapshotRegistry : IDisposable
    {
        private readonly Dictionary<int, StartupSweepSnapshots> _sets = new Dictionary<int, StartupSweepSnapshots>();
        private int _lastId;
        private int _openId;

        /// <summary>Sets not yet freed: the open one plus closed ones still held by jobs.</summary>
        public int LiveSetCount => _sets.Count;

        /// <summary>Opens a set for one sweep; until <see cref="Close"/>, <see cref="TakeForJob"/> hands it out.</summary>
        /// <param name="worldData">The chunks the snapshots are filled from.</param>
        /// <param name="allocator">The allocator for every snapshot.</param>
        /// <returns>False when a set is already open.</returns>
        public bool Begin(WorldData worldData, Allocator allocator)
        {
            if (_openId != 0) return false;

            do _lastId = _lastId == int.MaxValue ? 1 : _lastId + 1;
            while (_sets.ContainsKey(_lastId));

            _openId = _lastId;
            _sets.Add(_openId, new StartupSweepSnapshots(worldData, allocator));
            return true;
        }

        /// <summary>Retains the open set for one job.</summary>
        /// <param name="id">Receives the set's id for the job to keep, or 0 when none is open.</param>
        /// <returns>The open set, or null when none is open.</returns>
        public StartupSweepSnapshots TakeForJob(out int id)
        {
            id = _openId;
            if (id == 0) return null;

            StartupSweepSnapshots set = _sets[id];
            set.Retain();
            return set;
        }

        /// <summary>Closes the open set; it is freed with the last job still holding it.</summary>
        /// <returns>The maps the set filled, or 0 when none was open.</returns>
        public int Close()
        {
            if (_openId == 0) return 0;

            int id = _openId;
            _openId = 0;
            StartupSweepSnapshots set = _sets[id];
            int filled = set.SnapshotCount;
            set.Close();
            if (set.IsDisposed) _sets.Remove(id);
            return filled;
        }

        /// <summary>Drops one released job's reference, freeing its set with its last job. Unknown ids are ignored.</summary>
        /// <param name="id">The id <see cref="TakeForJob"/> gave the job.</param>
        public void Release(int id)
        {
            if (!_sets.TryGetValue(id, out StartupSweepSnapshots set)) return;

            set.Release();
            if (set.IsDisposed) _sets.Remove(id);
        }

        /// <summary>Counts the open set's maps that no longer match their live chunk (diagnostic).</summary>
        /// <returns>The stale maps, or 0 when no set is open.</returns>
        public int CountStaleInOpenSet() => _openId == 0 ? 0 : _sets[_openId].CountStaleSnapshots();

        /// <summary>Frees every set, open or held. Only safe once no scheduled job still reads them.</summary>
        public void Dispose()
        {
            foreach (StartupSweepSnapshots set in _sets.Values) set.Dispose();
            _sets.Clear();
            _openId = 0;
        }
    }
}
