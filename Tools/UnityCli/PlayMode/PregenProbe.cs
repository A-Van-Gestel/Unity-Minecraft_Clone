// CMD-6 station-walk prototype (Documentation/Design/CHUNK_PREGENERATION.md §2.2): walks the player anchor
// through a serpentine station grid over a chunk rectangle and waits, per station, until every missing chunk in
// its cover square is populated (and, with waitLit, initial-lit with no job in flight). PG-2 replaces its
// private walk with calls into the real ChunkPregenerator and keeps it as the Play-mode harness.
//
// RUN     unity command run_script --file Tools/UnityCli/PlayMode/PregenProbe.cs --entry UnityCli.PregenProbe.Start
//         --args '[minX, minZ, maxX, maxZ, exMinX, exMinZ, exMaxX, exMaxZ, waitLit]' --result-only
//         Chunk coordinates; the ex* rectangle is excluded from the missing set (pass 0,0,-1,-1 for none).
// READ    Each run_script call compiles a NEW assembly with fresh statics, so read progress by reflection into the
//         assembly Start reported (assemblyName), e.g. unity command eval "foreach (var a in
//         System.AppDomain.CurrentDomain.GetAssemblies()) { if (!a.GetName().Name.StartsWith(\"<assemblyName>\"))
//         continue; return (string)a.GetType(\"UnityCli.PregenProbe\").GetMethod(\"Report\").Invoke(null, null); }
//         return \"not found\";" --result-only
// NEEDS   A loaded World in Play mode (WorldRig.Launch), with /fly and /noclip on. Entering Play mode is the user's call.

using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Data;
using Helpers;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace UnityCli
{
    /// <summary>
    /// Throwaway feasibility probe for CMD-6: walks the player anchor through a serpentine grid of stations over a
    /// chunk rectangle and, at each, waits until every still-missing target in the station's cover square is
    /// populated (optionally: initial lighting scheduled + no lighting job in flight), then moves on.
    /// </summary>
    public static class PregenProbe
    {
        private const double STALL_SECONDS = 20.0;

        private static readonly List<ChunkCoord> s_stations = new List<ChunkCoord>();
        private static readonly HashSet<ChunkCoord> s_missing = new HashSet<ChunkCoord>();
        private static readonly List<ChunkCoord> s_scratch = new List<ChunkCoord>();
        private static readonly StringBuilder s_log = new StringBuilder();
        private static Stopwatch s_total;
        private static Stopwatch s_station;
        private static int s_index;
        private static int s_radius;
        private static bool s_waitLit;
        private static bool s_running;
        private static int s_initialMissing;
        private static int s_lastRemaining;
        private static double s_lastProgressAt;
        private static int s_stalls;
        private static int s_frames;
        private static Transform s_player;

        /// <summary>Plans the station walk over the chunk rectangle and starts it; returns the plan summary.</summary>
        /// <param name="minX">Rectangle minimum chunk X.</param>
        /// <param name="minZ">Rectangle minimum chunk Z.</param>
        /// <param name="maxX">Rectangle maximum chunk X.</param>
        /// <param name="maxZ">Rectangle maximum chunk Z.</param>
        /// <param name="exMinX">Excluded rectangle minimum chunk X.</param>
        /// <param name="exMinZ">Excluded rectangle minimum chunk Z.</param>
        /// <param name="exMaxX">Excluded rectangle maximum chunk X.</param>
        /// <param name="exMaxZ">Excluded rectangle maximum chunk Z.</param>
        /// <param name="waitLit">Also wait for initial lighting to settle at each station.</param>
        /// <returns>The plan line, or the reason the walk could not start.</returns>
        public static string Start(int minX, int minZ, int maxX, int maxZ, int exMinX, int exMinZ, int exMaxX,
            int exMaxZ, bool waitLit)
        {
            if (!Application.isPlaying || World.Instance == null) return "no world";
            Player player = Object.FindAnyObjectByType<Player>();
            if (player == null) return "no player";
            s_player = player.transform;

            int load = World.Instance.settings.LoadDistance;
            s_waitLit = waitLit;
            s_radius = waitLit ? load - 1 : load;
            int stride = 2 * s_radius + 1;

            s_missing.Clear();
            for (int x = minX; x <= maxX; x++)
            for (int z = minZ; z <= maxZ; z++)
            {
                bool excluded = x >= exMinX && x <= exMaxX && z >= exMinZ && z <= exMaxZ;
                if (!excluded) s_missing.Add(new ChunkCoord(x, z));
            }

            s_initialMissing = s_missing.Count;

            // Serpentine station grid; only stations whose cover square holds a missing chunk are kept.
            s_stations.Clear();
            bool forward = true;
            for (int cz = minZ + s_radius; cz - s_radius <= maxZ; cz += stride)
            {
                List<int> row = new List<int>();
                for (int cx = minX + s_radius; cx - s_radius <= maxX; cx += stride) row.Add(cx);
                if (!forward) row.Reverse();
                forward = !forward;
                foreach (int cx in row)
                {
                    ChunkCoord c = new ChunkCoord(cx, cz);
                    if (CountTargets(c) > 0) s_stations.Add(c);
                }
            }

            s_log.Clear();
            s_log.AppendLine($"vd={World.Instance.settings.viewDistance} load={load} radius={s_radius} stride={stride} " +
                             $"waitLit={waitLit} missing={s_initialMissing} stations={s_stations.Count}");
            s_index = -1;
            s_stalls = 0;
            s_frames = 0;
            s_total = Stopwatch.StartNew();
            s_running = true;
            NextStation();
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            return s_log.ToString();
        }

        private static int CountTargets(ChunkCoord station)
        {
            int n = 0;
            foreach (ChunkCoord m in s_missing)
                if (Mathf.Abs(m.X - station.X) <= s_radius && Mathf.Abs(m.Z - station.Z) <= s_radius) n++;
            return n;
        }

        private static void NextStation()
        {
            s_index++;
            if (s_index >= s_stations.Count)
            {
                s_running = false;
                s_total.Stop();
                s_log.AppendLine($"DONE total={s_total.Elapsed.TotalSeconds:F1}s frames={s_frames} stalls={s_stalls} " +
                                 $"leftMissing={s_missing.Count}");
                EditorApplication.update -= Tick;
                return;
            }

            ChunkCoord c = s_stations[s_index];
            float y = VoxelData.ChunkHeight * 0.8f;
            s_player.position = WorldOrigin.VoxelToUnity(new Vector3(c.X * VoxelData.ChunkWidth + 8f, y,
                c.Z * VoxelData.ChunkWidth + 8f));
            s_station = Stopwatch.StartNew();
            s_lastRemaining = int.MaxValue;
            s_lastProgressAt = 0;
        }

        private static void Tick()
        {
            if (!s_running || !Application.isPlaying || World.Instance == null)
            {
                EditorApplication.update -= Tick;
                return;
            }

            s_frames++;
            ChunkCoord station = s_stations[s_index];
            WorldJobManager jobs = World.Instance.JobManager;

            s_scratch.Clear();
            int remaining = 0;
            foreach (ChunkCoord m in s_missing)
            {
                if (Mathf.Abs(m.X - station.X) > s_radius || Mathf.Abs(m.Z - station.Z) > s_radius) continue;
                bool done = World.Instance.worldData.TryGetChunk(m.ToVoxelOrigin(), out ChunkData d) && d.IsPopulated
                            && !jobs.GenerationJobs.ContainsKey(m);
                if (done && s_waitLit) done = !d.NeedsInitialLighting && !jobs.LightingJobs.ContainsKey(m);
                if (done) s_scratch.Add(m);
                else remaining++;
            }

            double now = s_station.Elapsed.TotalSeconds;
            if (remaining < s_lastRemaining)
            {
                s_lastRemaining = remaining;
                s_lastProgressAt = now;
            }

            if (remaining == 0 || now - s_lastProgressAt > STALL_SECONDS)
            {
                if (remaining > 0) s_stalls++;
                foreach (ChunkCoord m in s_scratch) s_missing.Remove(m);
                s_log.AppendLine($"station {s_index + 1}/{s_stations.Count} ({station.X},{station.Z}) " +
                                 $"done={s_scratch.Count} stuck={remaining} {now.ToString("F2", CultureInfo.InvariantCulture)}s " +
                                 $"t={s_total.Elapsed.TotalSeconds:F1}s resident={World.Instance.worldData.Chunks.Count}");
                NextStation();
            }
        }

        /// <summary>Returns the walk log so far, prefixed with RUNNING while it is active.</summary>
        public static string Report() => (s_running ? "RUNNING " : "") + s_log;

        /// <summary>Aborts the walk and returns the log so far.</summary>
        public static string Stop()
        {
            s_running = false;
            EditorApplication.update -= Tick;
            return "stopped\n" + s_log;
        }
    }
}
