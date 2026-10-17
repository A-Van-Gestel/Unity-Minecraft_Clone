// CMD-6 Play-mode harness (Documentation/Design/CHUNK_PREGENERATION.md §2.2, §7 PG-2): runs the real pre-generator
// (World.PregenerateRegion — the benchmark's path: no return to start, no closing save) over a chunk rectangle and,
// once it ends and the asynchronous unload saves have drained, checks independently of the driver that every chunk
// of the rectangle is either on disk (ChunkStorageManager.CollectSavedChunks) or resident and populated.
//
// RUN     unity command run_script --file Tools/UnityCli/PlayMode/PregenProbe.cs --entry UnityCli.PregenProbe.Start
//         --args '[minX, minZ, maxX, maxZ]' --result-only          (chunk coordinates, inclusive)
// READ    Each run_script call compiles a NEW assembly with fresh statics, so read progress by reflection into the
//         assembly Start reported (assemblyName), e.g. unity command eval "foreach (var a in
//         System.AppDomain.CurrentDomain.GetAssemblies()) { if (!a.GetName().Name.StartsWith(\"<assemblyName>\"))
//         continue; return (string)a.GetType(\"UnityCli.PregenProbe\").GetMethod(\"Report\").Invoke(null, null); }
//         return \"not found\";" --result-only
//         Report() starts with RUNNING while the walk or the post-check is still going; Stop() cancels the walk.
// NEEDS   A loaded World in Play mode (WorldRig.Launch). Entering Play mode is the user's call. The walk holds the
//         player itself, so /fly and /noclip are not needed.

using System.Collections.Generic;
using System.Text;
using Data;
using Serialization;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>Drives one real pre-generation run and verifies its result independently of the driver's report.</summary>
    public static class PregenProbe
    {
        private static readonly StringBuilder s_log = new StringBuilder();
        private static ChunkRect s_rect;
        private static PregenerationOutcome s_outcome;
        private static bool s_running;
        private static int s_progressLines;

        /// <summary>Starts a run over the chunk rectangle; returns the start line or the refusal.</summary>
        /// <param name="minX">Rectangle corner chunk X.</param>
        /// <param name="minZ">Rectangle corner chunk Z.</param>
        /// <param name="maxX">Opposite corner chunk X.</param>
        /// <param name="maxZ">Opposite corner chunk Z.</param>
        /// <returns>The start line (with this assembly's name for the Report read-back), or why it did not start.</returns>
        public static string Start(int minX, int minZ, int maxX, int maxZ)
        {
            World world = World.Instance;
            if (!Application.isPlaying || world == null) return "no world";

            s_rect = new ChunkRect(new ChunkCoord(minX, minZ), new ChunkCoord(maxX, maxZ));
            Helpers.PregenerationPlan.StartRefusal refusal = world.CheckPregenerationStart(s_rect);
            if (refusal != Helpers.PregenerationPlan.StartRefusal.None) return "refused: " + refusal;

            s_log.Clear();
            s_outcome = null;
            s_progressLines = 0;
            s_running = true;
            s_log.AppendLine($"vd={world.settings.viewDistance} load={world.settings.LoadDistance} rect={s_rect} " +
                             $"chunks={s_rect.Count}");

            world.PregenerationProgress += OnProgress;
            world.PregenerationEnded += OnEnded;
            world.StartCoroutine(world.PregenerateRegion(s_rect));

            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            return s_log + "assemblyName=" + typeof(PregenProbe).Assembly.GetName().Name;
        }

        private static void OnProgress(PregenerationProgressReport report)
        {
            s_progressLines++;
            s_log.AppendLine($"station {report.StationsChecked}/{report.StationCount}: {report.Generated} generated " +
                             $"t={Time.realtimeSinceStartup:F1}s resident={World.Instance.worldData.ChunkCount}");
        }

        private static void OnEnded(PregenerationOutcome outcome)
        {
            s_outcome = outcome;
            World.Instance.PregenerationProgress -= OnProgress;
            World.Instance.PregenerationEnded -= OnEnded;
            s_log.AppendLine($"ENDED {outcome.Status} missingFound={outcome.MissingFound} stations={outcome.Stations} " +
                             $"generated={outcome.Generated} elapsed={outcome.ElapsedSeconds:F1}s " +
                             $"populate={outcome.PopulateSeconds:F1}s settle={outcome.SettleSeconds:F1}s " +
                             $"slowest={outcome.SlowestStationSeconds:F1}s@{outcome.SlowestStation} stalled={outcome.Stalled} " +
                             $"settleTimeouts={outcome.SettleTimeouts} remarked={outcome.Remarked}" +
                             (outcome.CancelReason != null ? " cancel=" + outcome.CancelReason : ""));
        }

        private static void Tick()
        {
            World world = World.Instance;
            if (!s_running || !Application.isPlaying || world == null)
            {
                EditorApplication.update -= Tick;
                return;
            }

            if (s_outcome == null) return;

            // The post-check reads disk, so it waits until every fired save has either landed or failed.
            long fired = ChunkStorageManager.SavesFired;
            long settled = ChunkStorageManager.SavesCompleted + ChunkStorageManager.SavesFailed;
            if (settled < fired) return;

            HashSet<ChunkCoord> saved = new HashSet<ChunkCoord>();
            world.StorageManager.CollectSavedChunks(s_rect, saved);

            int absent = 0;
            List<ChunkCoord> firstAbsent = new List<ChunkCoord>();
            for (int z = s_rect.MinZ; z <= s_rect.MaxZ; z++)
            for (int x = s_rect.MinX; x <= s_rect.MaxX; x++)
            {
                ChunkCoord chunkCoord = new ChunkCoord(x, z);
                if (saved.Contains(chunkCoord)) continue;
                if (world.worldData.TryGetChunk(chunkCoord.ToVoxelOrigin(), out ChunkData data) && data.IsPopulated) continue;
                absent++;
                if (firstAbsent.Count < 8) firstAbsent.Add(chunkCoord);
            }

            s_log.AppendLine($"POSTCHECK saved={saved.Count} absent={absent}" +
                             (absent > 0 ? " first=" + string.Join(", ", firstAbsent) : "") +
                             $" savesFailed={ChunkStorageManager.SavesFailed}");
            s_running = false;
            EditorApplication.update -= Tick;
        }

        /// <summary>Returns the run log so far, prefixed with RUNNING until the post-check has run.</summary>
        /// <returns>The log, prefixed while the run is active.</returns>
        public static string Report() => (s_running ? "RUNNING " : "") + s_log;

        /// <summary>Cancels the walk; the post-check still runs on whatever finished.</summary>
        /// <returns>Whether a cancel was requested, followed by the log so far.</returns>
        public static string Stop()
        {
            bool canceled = World.Instance != null && World.Instance.CancelPregeneration("probe stop");
            return (canceled ? "cancel requested\n" : "no run active\n") + s_log;
        }
    }
}
